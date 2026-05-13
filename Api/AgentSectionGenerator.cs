using System.Diagnostics;
using Azure.AI.Extensions.OpenAI;
using Azure.AI.Projects;
using Azure.Core;
using Microsoft.Extensions.Logging;
using OpenAI.Responses;
using System.ClientModel;
using System.ClientModel.Primitives;

namespace Api;

public sealed class AgentSectionGenerator
{
    private readonly ILogger<AgentSectionGenerator> _logger;
    private readonly TokenCredential _credential;

    public AgentSectionGenerator(ILogger<AgentSectionGenerator> logger, TokenCredential credential)
    {
        _logger = logger;
        _credential = credential;
    }

    public async Task<string> GenerateSectionAsync(
        string consultDraft,
        string sectionName,
        string sectionStandard,
        CancellationToken cancellationToken)
    {
        var endpoint = Environment.GetEnvironmentVariable("AzureAI__Endpoint");
        var agentName = Environment.GetEnvironmentVariable("AzureAI__AgentName");
        var agentVersion = Environment.GetEnvironmentVariable("AzureAI__AgentVersion");
        var azureClientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
        var networkTimeoutSeconds = GetEnvironmentInt("AzureAI__NetworkTimeoutSeconds", 270);
        var maxRetries = GetEnvironmentInt("AzureAI__MaxRetries", 0);

        if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(agentName) || string.IsNullOrEmpty(agentVersion))
        {
            _logger.LogError(
                "Azure AI configuration missing. HasEndpoint={HasEndpoint}, HasAgentName={HasAgentName}, HasAgentVersion={HasAgentVersion}",
                !string.IsNullOrEmpty(endpoint),
                !string.IsNullOrEmpty(agentName),
                !string.IsNullOrEmpty(agentVersion));

            throw new InvalidOperationException("Azure AI configuration missing: AzureAI__Endpoint, AzureAI__AgentName, and AzureAI__AgentVersion are required");
        }

        var endpointUri = new Uri(endpoint);

        _logger.LogInformation(
            "Azure AI configuration loaded. EndpointHost={EndpointHost}, AgentName={AgentName}, AgentVersion={AgentVersion}, HasAzureClientId={HasAzureClientId}",
            endpointUri.Host,
            agentName,
            agentVersion,
            !string.IsNullOrWhiteSpace(azureClientId));

        var projectClientOptions = new AIProjectClientOptions
        {
            NetworkTimeout = TimeSpan.FromSeconds(networkTimeoutSeconds),
            RetryPolicy = new ClientRetryPolicy(maxRetries)
        };

        var userMessage = BuildUserMessage(consultDraft, sectionName, sectionStandard);
        var projectClient = new AIProjectClient(endpointUri, _credential, projectClientOptions);

        ResponseResult sdkResponse;

        try
        {
            _logger.LogInformation(
                "Sending Foundry agent request. SectionName={SectionName}, AgentName={AgentName}, AgentVersion={AgentVersion}, MessageLength={MessageLength}",
                sectionName,
                agentName,
                agentVersion,
                userMessage.Length);

            var sdkStopwatch = Stopwatch.StartNew();
            sdkResponse = await CreateAgentResponseAsync(
                projectClient.ProjectOpenAIClient,
                new AgentReference(agentName, agentVersion),
                userMessage,
                cancellationToken);

            _logger.LogInformation(
                "Foundry agent request completed with version. SectionName={SectionName}, ElapsedMs={ElapsedMs}",
                sectionName,
                sdkStopwatch.ElapsedMilliseconds);
        }
        catch (ClientResultException ex) when (ex.Status == 400)
        {
            _logger.LogWarning(ex, "Foundry SDK rejected agent version payload. Retrying without agent_reference.version.");

            var retryStopwatch = Stopwatch.StartNew();
            sdkResponse = await CreateAgentResponseAsync(
                projectClient.ProjectOpenAIClient,
                new AgentReference(agentName),
                userMessage,
                cancellationToken);

            _logger.LogInformation(
                "Foundry agent request completed without version. SectionName={SectionName}, ElapsedMs={ElapsedMs}",
                sectionName,
                retryStopwatch.ElapsedMilliseconds);
        }

        var assistantText = sdkResponse.GetOutputText();

        if (string.IsNullOrWhiteSpace(assistantText))
        {
            _logger.LogError("Foundry response did not contain assistant text. SectionName={SectionName}", sectionName);
            throw new InvalidOperationException("No assistant response found");
        }

        return assistantText;
    }

    private static string BuildUserMessage(string consultDraft, string sectionName, string sectionStandard)
    {
        return $"""
            You are writing one section of an oncology consult note.

            Source of truth:
            Use only the clinical facts contained in the draft consult note below.

            Section standard:
            The following standard shows the desired organization, tone, level of detail, and clinical writing style.
            It is not patient data. Do not copy diagnoses, staging, receptor values, dates, pathology details, treatments, scores, symptoms, or outcomes from the standard unless they are also present in the draft consult note.

            Missing information rule:
            Do not invent missing pathology, dates, staging, receptor status, genomic scores, medications, allergies, physical exam findings, or treatment decisions.
            If a detail is not present in the draft, omit it unless the section would be misleading without it, in which case write "not documented."

            Output rule:
            Return only the final prose for the requested section. Do not include a heading, JSON, markdown, bullets, or commentary.

            Requested section:
            {sectionName}

            Section standard:
            {sectionStandard}

            Draft consult note:
            {consultDraft}
            """;
    }

    private static async Task<ResponseResult> CreateAgentResponseAsync(
        ProjectOpenAIClient projectOpenAIClient,
        AgentReference agentReference,
        string userMessage,
        CancellationToken cancellationToken)
    {
        var responsesClient = projectOpenAIClient.GetProjectResponsesClientForAgent(agentReference);
        var options = new CreateResponseOptions
        {
            StoredOutputEnabled = false
        };

        options.InputItems.Add(ResponseItem.CreateUserMessageItem(userMessage));

        var response = await responsesClient.CreateResponseAsync(options, cancellationToken);
        return response.Value;
    }

    private static int GetEnvironmentInt(string name, int defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);

        if (int.TryParse(value, out var parsed) && parsed >= 0)
        {
            return parsed;
        }

        return defaultValue;
    }
}
