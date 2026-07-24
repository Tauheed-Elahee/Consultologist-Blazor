using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask.Client;

namespace Consultologist.Api.Email;

public sealed class EmailIntakeFunctions
{
    private readonly EmailIntakeProcessor _processor;

    public EmailIntakeFunctions(EmailIntakeProcessor processor)
    {
        _processor = processor;
    }

    // The schedule setting must resolve in every environment or the host fails
    // to index the app; EmailIntake__MailboxAddress (unset → quiet no-op) is
    // the on/off switch, not the schedule.
    [Function("EmailIntakePoll")]
    public Task RunAsync(
        [TimerTrigger("%EmailIntake__PollSchedule%")] TimerInfo timer,
        [DurableClient] DurableTaskClient client,
        FunctionContext context)
        => _processor.RunOnceAsync(client, context.CancellationToken);
}
