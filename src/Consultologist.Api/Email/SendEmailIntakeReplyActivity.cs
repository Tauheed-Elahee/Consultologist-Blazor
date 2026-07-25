using Consultologist.Api.Auth;
using Consultologist.Api.Jobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Consultologist.Api.Email;

public sealed record EmailIntakeReplyInput(
    string JobId,
    string ToAddress,
    string FinalStatus,
    // #159: when present alongside a set delivery password, the completed
    // document travels as an encrypted attachment. Trailing optional members —
    // Durable payload compatibility.
    string? AppUserId = null,
    string? AssembledDocument = null);

/// <summary>
/// The completion reply (#158/#157): a fresh no-PHI message — never a Graph
/// /reply, which would quote the PHI-bearing original — whose body is
/// boilerplate plus the History deep link. #159: when the account has set a
/// delivery password and the job produced an assembled document, the document
/// is attached as an AES-256 password-protected PDF; any failure in that leg
/// degrades to the link-only reply, never to silence.
/// </summary>
public sealed class SendEmailIntakeReplyActivity
{
    public const string Name = "send-email-intake-reply";

    private readonly IGraphMailClient _mail;
    private readonly IAccountSettingsStore _settingsStore;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SendEmailIntakeReplyActivity> _logger;

    public SendEmailIntakeReplyActivity(
        IGraphMailClient mail,
        IAccountSettingsStore settingsStore,
        IConfiguration configuration,
        ILogger<SendEmailIntakeReplyActivity> logger)
    {
        _mail = mail;
        _settingsStore = settingsStore;
        _configuration = configuration;
        _logger = logger;
    }

    [Function(Name)]
    public async Task RunAsync([ActivityTrigger] EmailIntakeReplyInput input, FunctionContext context)
    {
        var mailbox = _configuration["EmailIntake:MailboxAddress"];
        var appBaseUrl = _configuration["EmailIntake:AppBaseUrl"];

        if (string.IsNullOrWhiteSpace(mailbox) || string.IsNullOrWhiteSpace(appBaseUrl))
        {
            _logger.LogWarning(
                "Email intake reply skipped: EmailIntake settings incomplete. JobId={JobId}",
                input.JobId);
            return;
        }

        var attachment = await TryCreateAttachmentAsync(input, context.CancellationToken);

        var (subject, body) = EmailIntakeReply.Compose(
            appBaseUrl,
            input.JobId,
            input.FinalStatus,
            includesAttachment: attachment != null);
        await _mail.SendMailAsync(mailbox, input.ToAddress, subject, body, context.CancellationToken, attachment);

        _logger.LogInformation(
            "Email intake reply sent. JobId={JobId}, FinalStatus={FinalStatus}, Attached={Attached}",
            input.JobId,
            input.FinalStatus,
            attachment != null);
    }

    private async Task<GraphMailAttachment?> TryCreateAttachmentAsync(
        EmailIntakeReplyInput input,
        CancellationToken cancellationToken)
    {
        if (input.FinalStatus != ConsultGenerationJobStatuses.Completed
            || string.IsNullOrWhiteSpace(input.AssembledDocument)
            || string.IsNullOrWhiteSpace(input.AppUserId))
        {
            return null;
        }

        try
        {
            var password = await _settingsStore.GetAsync(
                input.AppUserId,
                AccountSettingKeys.DeliveryPassword,
                cancellationToken);

            if (string.IsNullOrEmpty(password?.Value))
            {
                // Explicit over default: no password → link-only reply.
                return null;
            }

            var pdf = ConsultDocumentPdf.Render(input.AssembledDocument, password.Value);
            // Filename carries no PHI — just the short job id.
            return new GraphMailAttachment($"consult-{input.JobId[..Math.Min(8, input.JobId.Length)]}.pdf", pdf);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Encrypted attachment could not be produced; sending link-only reply. JobId={JobId}",
                input.JobId);
            return null;
        }
    }
}

internal static class EmailIntakeReply
{
    internal static (string Subject, string Body) Compose(
        string appBaseUrl,
        string jobId,
        string finalStatus,
        bool includesAttachment = false)
    {
        var link = $"{appBaseUrl.TrimEnd('/')}/history/{jobId}";

        if (finalStatus == ConsultGenerationJobStatuses.Completed)
        {
            var attachmentNote = includesAttachment
                ? "The consult document is attached, encrypted with your delivery password.\n\n"
                : string.Empty;

            return (
                "Your consult is ready",
                "Your consult has finished processing.\n\n"
                + attachmentNote
                + "View the result in Consultologist History (sign-in required):\n"
                + link + "\n\n"
                + (includesAttachment
                    ? "The message body intentionally contains no clinical content.\n"
                    : "This message intentionally contains no clinical content.\n")
                + "If you did not submit this consult, please delete this message.");
        }

        return (
            "Your consult run did not complete",
            "Your consult could not be completed.\n\n"
            + "Details are in Consultologist History (sign-in required):\n"
            + link + "\n\n"
            + "You can submit the consult again to retry.\n"
            + "This message intentionally contains no clinical content.");
    }
}
