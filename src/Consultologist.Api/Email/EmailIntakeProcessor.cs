using Consultologist.Api.Jobs;
using Consultologist.Api.Workflow;
using Consultologist.Api.Models;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Consultologist.Api.Email;

public sealed record EmailIntakeRunSummary(int Listed, int Accepted, int Rejected, int Skipped, int Repaired);

/// <summary>
/// The email-intake pipeline (#158, docs/ASYNC_DELIVERY.md §2). Per message:
/// claim FIRST (at-most-once for PHI jobs — a crash between claim and start
/// drops the message visibly rather than ever running it twice), then the
/// authentication floor, the sender gate, and the job start; disposition moves
/// the message to Processed/Rejected. Rejections are silent — no bounce, no
/// backscatter — and logged with metadata only, never subject or body.
/// </summary>
public sealed class EmailIntakeProcessor
{
    internal const string ProcessedFolder = "Processed";
    internal const string RejectedFolder = "Rejected";
    private const int DefaultMaxMessagesPerPoll = 25;
    private const int MaxDraftLength = 256 * 1024;
    // #210: per attachment, mirroring the body cap and the HTTP per-input
    // bound; the total keeps one message from pulling unbounded bytes.
    private const int MaxAttachmentLength = 256 * 1024;
    private const int MaxTotalAttachmentBytes = 1024 * 1024;
    private static readonly string[] ReadableAttachmentExtensions = { ".txt", ".md" };
    private static readonly TimeSpan StaleClaimAge = TimeSpan.FromMinutes(10);

    private readonly IConfiguration _configuration;
    private readonly IGraphMailClient _mail;
    private readonly IEmailSenderResolver _senderResolver;
    private readonly IEmailIntakeClaimStore _claims;
    private readonly IConsultGenerationJobStarter _jobStarter;
    private readonly IWorkflowPackagePinResolver _pinResolver;
    private readonly IWorkflowPackageStore _packageStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EmailIntakeProcessor> _logger;

    public EmailIntakeProcessor(
        IConfiguration configuration,
        IGraphMailClient mail,
        IEmailSenderResolver senderResolver,
        IEmailIntakeClaimStore claims,
        IConsultGenerationJobStarter jobStarter,
        IWorkflowPackagePinResolver pinResolver,
        IWorkflowPackageStore packageStore,
        TimeProvider timeProvider,
        ILogger<EmailIntakeProcessor> logger)
    {
        _configuration = configuration;
        _mail = mail;
        _senderResolver = senderResolver;
        _claims = claims;
        _jobStarter = jobStarter;
        _pinResolver = pinResolver;
        _packageStore = packageStore;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<EmailIntakeRunSummary> RunOnceAsync(DurableTaskClient client, CancellationToken cancellationToken)
    {
        var mailbox = _configuration["EmailIntake:MailboxAddress"];

        if (string.IsNullOrWhiteSpace(mailbox))
        {
            // The kill-switch: no mailbox configured (local dev, CI) → quiet no-op.
            _logger.LogDebug("Email intake mailbox not configured; poll skipped.");
            return new EmailIntakeRunSummary(0, 0, 0, 0, 0);
        }

        var top = _configuration.GetValue("EmailIntake:MaxMessagesPerPoll", DefaultMaxMessagesPerPoll);
        var refs = await _mail.ListUnreadInboxMessagesAsync(mailbox, top, cancellationToken);

        int accepted = 0, rejected = 0, skipped = 0, repaired = 0;

        foreach (var messageRef in refs)
        {
            var outcome = await ProcessMessageAsync(client, mailbox, messageRef, cancellationToken);
            switch (outcome)
            {
                case MessageOutcome.Accepted: accepted++; break;
                case MessageOutcome.Rejected: rejected++; break;
                case MessageOutcome.Skipped: skipped++; break;
                case MessageOutcome.Repaired: repaired++; break;
            }
        }

        var summary = new EmailIntakeRunSummary(refs.Count, accepted, rejected, skipped, repaired);

        if (refs.Count > 0)
        {
            _logger.LogInformation(
                "Email intake poll complete. Listed={Listed}, Accepted={Accepted}, Rejected={Rejected}, Skipped={Skipped}, Repaired={Repaired}",
                summary.Listed,
                summary.Accepted,
                summary.Rejected,
                summary.Skipped,
                summary.Repaired);
        }

        return summary;
    }

    private enum MessageOutcome { Accepted, Rejected, Skipped, Repaired }

    private async Task<MessageOutcome> ProcessMessageAsync(
        DurableTaskClient client,
        string mailbox,
        GraphMessageRef messageRef,
        CancellationToken cancellationToken)
    {
        var claimKey = messageRef.InternetMessageId ?? messageRef.Id;
        var now = _timeProvider.GetUtcNow();

        var claimed = await _claims.TryClaimAsync(
            new EmailIntakeClaim(claimKey, messageRef.Id, null, now),
            cancellationToken);

        if (!claimed)
        {
            return await RepairAsync(mailbox, messageRef, claimKey, now, cancellationToken);
        }

        var message = await _mail.GetMessageAsync(mailbox, messageRef.Id, cancellationToken);

        if (message == null)
        {
            await _claims.UpdateAsync(
                new EmailIntakeClaim(claimKey, messageRef.Id, null, now, Outcome: EmailIntakeOutcomes.Vanished),
                cancellationToken);
            return MessageOutcome.Skipped;
        }

        var verdict = EmailAuthenticationResults.Evaluate(message.InternetMessageHeaders);

        if (!verdict.Passes)
        {
            _logger.LogWarning(
                "Email intake rejected: authentication floor not met. From={From}, InternetMessageId={InternetMessageId}, Dmarc={Dmarc}, Spf={Spf}, Dkim={Dkim}",
                message.FromAddress,
                claimKey,
                verdict.Dmarc,
                verdict.Spf,
                verdict.Dkim);
            return await RejectAsync(mailbox, message, claimKey, now, EmailIntakeOutcomes.RejectedAuth, cancellationToken);
        }

        // The authenticated From — never Reply-To, which is attacker-choosable.
        var match = message.FromAddress == null
            ? new EmailSenderMatch(EmailSenderMatchOutcome.NoMatch)
            : await _senderResolver.ResolveAsync(message.FromAddress, cancellationToken);

        if (match.Outcome != EmailSenderMatchOutcome.Matched)
        {
            _logger.LogWarning(
                "Email intake rejected: sender gate. From={From}, InternetMessageId={InternetMessageId}, MatchOutcome={MatchOutcome}",
                message.FromAddress,
                claimKey,
                match.Outcome);
            return await RejectAsync(mailbox, message, claimKey, now, EmailIntakeOutcomes.RejectedSender, cancellationToken);
        }

        var draft = message.BodyText?.Trim();

        if (draft is { Length: > MaxDraftLength })
        {
            _logger.LogWarning(
                "Email intake rejected: body over the size bound. From={From}, InternetMessageId={InternetMessageId}, BodyLength={BodyLength}",
                message.FromAddress,
                claimKey,
                draft.Length);
            return await RejectAsync(mailbox, message, claimKey, now, EmailIntakeOutcomes.RejectedEmpty, cancellationToken);
        }

        // #210: attachments can carry the referral, so a blank body is only
        // fatal when nothing else arrived — the resolver decides that.
        var (attachments, attachmentError) = message.HasAttachments
            ? await ReadAttachmentsAsync(mailbox, message.Id, cancellationToken)
            : (Array.Empty<EmailInputAttachment>(), null);

        if (attachmentError != null)
        {
            _logger.LogWarning(
                "Email intake rejected: unusable attachment. From={From}, InternetMessageId={InternetMessageId}, Detail={Detail}",
                message.FromAddress,
                claimKey,
                attachmentError);
            return await RejectWithReplyAsync(
                mailbox, message, claimKey, now, match.AppUserId, EmailIntakeOutcomes.RejectedAttachments, cancellationToken);
        }

        var resolution = EmailAttachmentInputs.Resolve(
            await DeclaredInputIdsAsync(match.AppUserId!, cancellationToken),
            draft,
            attachments);

        if (resolution.RejectReason != null)
        {
            _logger.LogWarning(
                "Email intake rejected: inputs could not be assigned. From={From}, InternetMessageId={InternetMessageId}, Detail={Detail}",
                message.FromAddress,
                claimKey,
                resolution.RejectReason);
            var slug = attachments.Count > 0
                ? EmailIntakeOutcomes.RejectedAttachments
                : EmailIntakeOutcomes.RejectedEmpty;
            return attachments.Count > 0
                ? await RejectWithReplyAsync(mailbox, message, claimKey, now, match.AppUserId, slug, cancellationToken)
                : await RejectAsync(mailbox, message, claimKey, now, slug, cancellationToken);
        }

        var start = await _jobStarter.StartAsync(
            client,
            new ConsultGenerationRequest(
                null,
                Inputs: resolution.Inputs!.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)),
            match.AppUserId!,
            new ConsultGenerationJobOrigin(ConsultGenerationJobSources.Email, message.FromAddress),
            cancellationToken);

        if (start.Error != null)
        {
            _logger.LogError(
                "Email intake job start failed. From={From}, InternetMessageId={InternetMessageId}, Error={Error}",
                message.FromAddress,
                claimKey,
                start.Error);
            var outcome = start.Error == ConsultGenerationJobStartError.InputsMismatch
                ? EmailIntakeOutcomes.RejectedInputs
                : EmailIntakeOutcomes.StartFailed;
            await _claims.UpdateAsync(
                new EmailIntakeClaim(claimKey, message.Id, message.FromAddress, now, match.AppUserId, Outcome: outcome),
                cancellationToken);
            await DisposeMessageAsync(mailbox, message.Id, RejectedFolder, cancellationToken);
            await SendStartFailureReplyAsync(mailbox, message.FromAddress!, cancellationToken);
            return MessageOutcome.Rejected;
        }

        await DisposeMessageAsync(mailbox, message.Id, ProcessedFolder, cancellationToken);
        await _claims.UpdateAsync(
            new EmailIntakeClaim(claimKey, message.Id, message.FromAddress, now, match.AppUserId, start.JobId, EmailIntakeOutcomes.Accepted),
            cancellationToken);

        _logger.LogInformation(
            "Email intake accepted. JobId={JobId}, InternetMessageId={InternetMessageId}",
            start.JobId,
            claimKey);

        return MessageOutcome.Accepted;
    }

    private async Task<MessageOutcome> RepairAsync(
        string mailbox,
        GraphMessageRef messageRef,
        string claimKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var existing = await _claims.GetAsync(claimKey, cancellationToken);

        if (existing == null)
        {
            // Claim add raced with a delete-less 409 anomaly; leave for next tick.
            return MessageOutcome.Skipped;
        }

        if (existing.Outcome == null && now - existing.ClaimedAtUtc < StaleClaimAge)
        {
            // The winner is still in flight (this tick or a concurrent host).
            return MessageOutcome.Skipped;
        }

        if (existing.Outcome == null)
        {
            // The winner crashed between claim and start: the message never ran
            // and never will — the visible cost of the at-most-once bias.
            _logger.LogWarning(
                "Email intake repairing a crashed claim; the message was NOT processed. InternetMessageId={InternetMessageId}, ClaimedAtUtc={ClaimedAtUtc}",
                claimKey,
                existing.ClaimedAtUtc);
        }

        var folder = existing.JobId != null ? ProcessedFolder : RejectedFolder;
        await DisposeMessageAsync(mailbox, messageRef.Id, folder, cancellationToken);
        return MessageOutcome.Repaired;
    }

    /// <summary>
    /// The declared input ids of the account's pinned package, in declaration
    /// order — what positional assignment counts against. Empty for v5/v6 and
    /// whenever the package cannot be resolved: the starter re-resolves and is
    /// the authority on legality, so a miss here degrades to legacy assignment
    /// rather than failing the message. The store caches resolved versions, so
    /// this is a cache hit by the time the starter asks again.
    /// </summary>
    private async Task<IReadOnlyList<string>> DeclaredInputIdsAsync(string appUserId, CancellationToken cancellationToken)
    {
        try
        {
            var packageRef = await _pinResolver.ResolvePinAsync(appUserId, cancellationToken);
            var package = await _packageStore.ResolveAsync(packageRef, cancellationToken);

            return package.Manifest.Inputs?.Select(input => input.Id).ToList()
                ?? (IReadOnlyList<string>)Array.Empty<string>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Email intake could not read the pinned package's declared inputs; assigning as legacy.");
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Decodes the message's non-inline attachments to text. Anything this
    /// version cannot read fails the whole message rather than being skipped:
    /// a referral that lived in the attachment would otherwise produce a
    /// consult from a body that says only "please see attached".
    /// </summary>
    private async Task<(IReadOnlyList<EmailInputAttachment> Attachments, string? Error)> ReadAttachmentsAsync(
        string mailbox,
        string messageId,
        CancellationToken cancellationToken)
    {
        var fetched = await _mail.ListAttachmentsAsync(mailbox, messageId, cancellationToken);
        var attachments = new List<EmailInputAttachment>();
        var totalBytes = 0L;

        foreach (var attachment in fetched)
        {
            var extension = Path.GetExtension(attachment.Name);

            if (!ReadableAttachmentExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                // PDF is the known next step (#208 phase 2), not a permanent
                // exclusion — the log says which type arrived.
                return (Array.Empty<EmailInputAttachment>(), $"unsupported type '{extension}'");
            }

            if (attachment.Content.Length > MaxAttachmentLength)
            {
                return (Array.Empty<EmailInputAttachment>(), "attachment over the size bound");
            }

            totalBytes += attachment.Content.Length;

            if (totalBytes > MaxTotalAttachmentBytes)
            {
                return (Array.Empty<EmailInputAttachment>(), "attachments over the total size bound");
            }

            attachments.Add(new EmailInputAttachment(
                attachment.Name,
                System.Text.Encoding.UTF8.GetString(attachment.Content)));
        }

        return (attachments, null);
    }

    /// <summary>
    /// A rejection the sender is told about: attachment problems are things
    /// they can fix by resending, unlike the silent auth and sender gates.
    /// </summary>
    private async Task<MessageOutcome> RejectWithReplyAsync(
        string mailbox,
        GraphMessage message,
        string claimKey,
        DateTimeOffset now,
        string? appUserId,
        string outcome,
        CancellationToken cancellationToken)
    {
        await _claims.UpdateAsync(
            new EmailIntakeClaim(claimKey, message.Id, message.FromAddress, now, appUserId, Outcome: outcome),
            cancellationToken);
        await DisposeMessageAsync(mailbox, message.Id, RejectedFolder, cancellationToken);

        if (message.FromAddress != null)
        {
            await SendStartFailureReplyAsync(mailbox, message.FromAddress, cancellationToken);
        }

        return MessageOutcome.Rejected;
    }

    private async Task<MessageOutcome> RejectAsync(
        string mailbox,
        GraphMessage message,
        string claimKey,
        DateTimeOffset now,
        string outcome,
        CancellationToken cancellationToken)
    {
        await _claims.UpdateAsync(
            new EmailIntakeClaim(claimKey, message.Id, message.FromAddress, now, Outcome: outcome),
            cancellationToken);
        await DisposeMessageAsync(mailbox, message.Id, RejectedFolder, cancellationToken);
        return MessageOutcome.Rejected;
    }

    private async Task DisposeMessageAsync(string mailbox, string messageId, string folder, CancellationToken cancellationToken)
    {
        await _mail.MarkReadAsync(mailbox, messageId, cancellationToken);
        var folderId = await _mail.EnsureInboxChildFolderAsync(mailbox, folder, cancellationToken);
        await _mail.MoveMessageAsync(mailbox, messageId, folderId, cancellationToken);
    }

    private async Task SendStartFailureReplyAsync(string mailbox, string toAddress, CancellationToken cancellationToken)
    {
        try
        {
            var appBaseUrl = _configuration["EmailIntake:AppBaseUrl"]?.TrimEnd('/');
            var body = "Your consult submitted by email could not be processed.\n\n"
                + "You can re-send the consult email to try again"
                + (appBaseUrl == null ? "." : $", or use the app directly:\n{appBaseUrl}\n")
                + "\nThis message intentionally contains no clinical content.";

            await _mail.SendMailAsync(mailbox, toAddress, "Your consult email could not be processed", body, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Email intake start-failure reply could not be sent.");
        }
    }
}
