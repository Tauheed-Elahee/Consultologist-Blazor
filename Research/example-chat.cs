using Azure;
using Azure.Identity;
using Azure.AI.Projects;
using Azure.AI.Agents.Persistent;

async Task RunAgentConversation()
{
    var endpoint = new Uri("https://constultologist-eastus2-resource.services.ai.azure.com/api/projects/constultologist-eastus2");
    AIProjectClient projectClient = new(endpoint, new DefaultAzureCredential());

    PersistentAgentsClient agentsClient = projectClient.GetPersistentAgentsClient();

    PersistentAgent agent = agentsClient.Administration.GetAgent("asst_gl4fbjg8eQHv1P3lHdUDdZvx");

    PersistentAgentThread thread = agentsClient.Threads.CreateThread();
    Console.WriteLine($"Created thread, ID: {thread.Id}");

    PersistentThreadMessage messageResponse = agentsClient.Messages.CreateMessage(
        thread.Id,
        MessageRole.User,
        "Hi Consultologist-demo");

    ThreadRun run = agentsClient.Runs.CreateRun(
        thread.Id,
        agent.Id);

    // Poll until the run reaches a terminal status
    do
    {
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        run = agentsClient.Runs.GetRun(thread.Id, run.Id);
    }
    while (run.Status == RunStatus.Queued
        || run.Status == RunStatus.InProgress);
    if (run.Status != RunStatus.Completed)
    {
        throw new InvalidOperationException($"Run failed or was canceled: {run.LastError?.Message}");
    }

    Pageable<PersistentThreadMessage> messages = agentsClient.Messages.GetMessages(
        thread.Id, order: ListSortOrder.Ascending);

    // Display messages
    foreach (PersistentThreadMessage threadMessage in messages)
    {
        Console.Write($"{threadMessage.CreatedAt:yyyy-MM-dd HH:mm:ss} - {threadMessage.Role,10}: ");
        foreach (MessageContent contentItem in threadMessage.ContentItems)
        {
            if (contentItem is MessageTextContent textItem)
            {
                Console.Write(textItem.Text);
            }
            else if (contentItem is MessageImageFileContent imageFileItem)
            {
                Console.Write($"<image from ID: {imageFileItem.FileId}");
            }
            Console.WriteLine();
        }
    }
}

// Main execution
await RunAgentConversation();
