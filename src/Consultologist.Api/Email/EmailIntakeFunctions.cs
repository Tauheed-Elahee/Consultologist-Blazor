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

    // Flat name by necessity: %…% binding expressions resolve literal config
    // keys, and the environment provider normalizes double-underscore names to
    // EmailIntake:PollSchedule — so a __ name can never resolve here. Unset →
    // this one function fails indexing and is disabled (host unaffected);
    // EmailIntake__MailboxAddress (unset → quiet no-op) is the real switch.
    [Function("EmailIntakePoll")]
    public Task RunAsync(
        [TimerTrigger("%EmailIntakePollSchedule%")] TimerInfo timer,
        [DurableClient] DurableTaskClient client,
        FunctionContext context)
        => _processor.RunOnceAsync(client, context.CancellationToken);
}
