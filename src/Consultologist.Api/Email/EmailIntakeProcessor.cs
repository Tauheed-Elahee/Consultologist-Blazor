using Consultologist.Api.Jobs;
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
    private static readonly TimeSpan StaleClaimAge = TimeSpan.FromMinutes(10);

    private readonly IConfiguration _configuration;
    private readonly IGraphMailClient _mail;
    private readonly IEmailSenderResolver _senderResolver;
    private readonly IEmailIntakeClaimStore _claims;
    private readonly IConsultGenerationJobStarter _jobStarter;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EmailIntakeProcessor> _logger;

    public EmailIntakeProcessor(
        IConfiguration configuration,
        IGraphMailClient mail,
        IEmailSenderResolver senderResolver,
        IEmailIntakeClaimStore claims,
        IConsultGenerationJobStarter jobStarter,
        TimeProvider timeProvider,
        ILogger<EmailIntakeProcessor> logger)
    {
        _configuration = configuration;
        _mail = mail;
        _senderResolver = senderResolver;
        _claims = claims;
        _jobStarter = jobStarter;
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

        if (string.IsNullOrWhiteSpace(draft) || draft.Length > MaxDraftLength)
        {
            _logger.LogWarning(
                "Email intake rejected: unusable body. From={From}, InternetMessageId={InternetMessageId}, BodyLength={BodyLength}",
                message.FromAddress,
                claimKey,
                draft?.Length ?? 0);
            return await RejectAsync(mailbox, message, claimKey, now, EmailIntakeOutcomes.RejectedEmpty, cancellationToken);
        }

        var start = await _jobStarter.StartAsync(
            client,
            new ConsultGenerationRequest(draft),
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
            await _claims.UpdateAsync(
                new EmailIntakeClaim(claimKey, message.Id, message.FromAddress, now, match.AppUserId, Outcome: EmailIntakeOutcomes.StartFailed),
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
