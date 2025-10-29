using System.Net;
using System.Text.Json;
using Azure;
using Azure.AI.Agents.Persistent;
using Azure.Identity;
using Api.Helpers;
using Api.Models;
using BlazorStaticWebApps.Shared;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BlazorStaticWebApps.Api;

public class ChatFunction
{
    private readonly ILogger<ChatFunction> _logger;
    private readonly IConfiguration _configuration;
    private readonly string _projectEndpoint;
    private readonly string _agentId;

    public ChatFunction(ILogger<ChatFunction> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
        _projectEndpoint = _configuration["AzureAIFoundry:ProjectEndpoint"]
            ?? throw new InvalidOperationException("AzureAIFoundry:ProjectEndpoint is not configured");
        _agentId = _configuration["AzureAIFoundry:AgentId"]
            ?? throw new InvalidOperationException("AzureAIFoundry:AgentId is not configured");
    }

    [Function("Chat")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "chat")] HttpRequestData req)
    {
        try
        {
            var principal = AuthenticationHelper.GetClientPrincipal(req);
            if (principal == null || string.IsNullOrEmpty(principal.UserId))
            {
                var unauthorizedResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauthorizedResponse.WriteStringAsync("User is not authenticated");
                return unauthorizedResponse;
            }

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var chatRequest = JsonSerializer.Deserialize<ChatRequest>(requestBody, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (chatRequest == null || string.IsNullOrWhiteSpace(chatRequest.Message))
            {
                var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequestResponse.WriteStringAsync("Message is required");
                return badRequestResponse;
            }

            var credential = new DefaultAzureCredential();
            var client = new PersistentAgentsClient(_projectEndpoint, credential);

            string threadId;
            if (!string.IsNullOrEmpty(chatRequest.ThreadId))
            {
                threadId = chatRequest.ThreadId;
                _logger.LogInformation("Using existing thread: {ThreadId}", threadId);
            }
            else
            {
                var thread = await client.Threads.CreateThreadAsync();
                threadId = thread.Value.Id;
                _logger.LogInformation("Created new thread: {ThreadId}", threadId);
            }

            await client.Messages.CreateMessageAsync(
                threadId,
                MessageRole.User,
                chatRequest.Message);

            var run = await client.Runs.CreateRunAsync(
                threadId,
                _agentId);

            while (run.Value.Status == RunStatus.Queued || run.Value.Status == RunStatus.InProgress)
            {
                await Task.Delay(1000);
                run = await client.Runs.GetRunAsync(threadId, run.Value.Id);
            }

            if (run.Value.Status != RunStatus.Completed)
            {
                _logger.LogError("Run failed with status: {Status}", run.Value.Status);
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new ChatResponse
                {
                    Success = false,
                    Error = $"Agent run failed with status: {run.Value.Status}",
                    ThreadId = threadId
                });
                return errorResponse;
            }

            var messages = client.Messages.GetMessagesAsync(threadId);
            PersistentThreadMessage? assistantMessage = null;
            await foreach (var message in messages)
            {
                if (message.Role == MessageRole.Agent)
                {
                    assistantMessage = message;
                    break;
                }
            }

            var responseMessage = assistantMessage?.ContentItems
                .OfType<MessageTextContent>()
                .FirstOrDefault()?.Text ?? "No response from agent";

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new ChatResponse
            {
                Success = true,
                Message = responseMessage,
                ThreadId = threadId
            });

            return response;
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "Azure AI request failed: {Message}", ex.Message);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new ChatResponse
            {
                Success = false,
                Error = $"Azure AI error: {ex.Message}"
            });
            return errorResponse;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing chat request: {Message}", ex.Message);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new ChatResponse
            {
                Success = false,
                Error = "An error occurred processing your request"
            });
            return errorResponse;
        }
    }
}
