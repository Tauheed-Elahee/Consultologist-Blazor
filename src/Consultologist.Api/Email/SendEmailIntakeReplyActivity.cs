using Consultologist.Api.Jobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Consultologist.Api.Email;

public sealed record EmailIntakeReplyInput(
    string JobId,
    string ToAddress,
    string FinalStatus);

/// <summary>
/// The completion reply for email-sourced jobs (#158): a fresh no-PHI message
/// — never a Graph /reply, which would quote the PHI-bearing original — whose
/// body is boilerplate plus the History deep link.
/// </summary>
public sealed class SendEmailIntakeReplyActivity
{
    public const string Name = "send-email-intake-reply";

    private readonly IGraphMailClient _mail;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SendEmailIntakeReplyActivity> _logger;

    public SendEmailIntakeReplyActivity(
        IGraphMailClient mail,
        IConfiguration configuration,
        ILogger<SendEmailIntakeReplyActivity> logger)
    {
        _mail = mail;
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

        var (subject, body) = EmailIntakeReply.Compose(appBaseUrl, input.JobId, input.FinalStatus);
        await _mail.SendMailAsync(mailbox, input.ToAddress, subject, body, context.CancellationToken);

        _logger.LogInformation(
            "Email intake reply sent. JobId={JobId}, FinalStatus={FinalStatus}",
            input.JobId,
            input.FinalStatus);
    }
}

internal static class EmailIntakeReply
{
    internal static (string Subject, string Body) Compose(string appBaseUrl, string jobId, string finalStatus)
    {
        var link = $"{appBaseUrl.TrimEnd('/')}/history/{jobId}";

        if (finalStatus == ConsultGenerationJobStatuses.Completed)
        {
            return (
                "Your consult is ready",
                "Your consult submitted by email has finished processing.\n\n"
                + "View the result in Consultologist History (sign-in required):\n"
                + link + "\n\n"
                + "This message intentionally contains no clinical content.\n"
                + "If you did not submit a consult by email, please delete this message.");
        }

        return (
            "Your consult run did not complete",
            "Your consult submitted by email could not be completed.\n\n"
            + "Details are in Consultologist History (sign-in required):\n"
            + link + "\n\n"
            + "You can re-send the consult email to try again.\n"
            + "This message intentionally contains no clinical content.");
    }
}
