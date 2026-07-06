using System.Diagnostics;
using Consultologist.Api.Models;
using Azure.AI.Extensions.OpenAI;
using Azure.AI.Projects;
using Azure.Core;
using Microsoft.Extensions.Logging;
using OpenAI.Responses;
using System.ClientModel;
using System.ClientModel.Primitives;

namespace Consultologist.Api.Agents;

public sealed class AgentSectionGenerator
{
    private readonly ILogger<AgentSectionGenerator> _logger;
    private readonly TokenCredential _credential;

    public AgentSectionGenerator(ILogger<AgentSectionGenerator> logger, TokenCredential credential)
    {
        _logger = logger;
        _credential = credential;

        Console.Error.WriteLine($"[Api.StartupDiagnostics] AgentSectionGenerator constructed. Utc={DateTimeOffset.UtcNow:O}");
        _logger.LogInformation("AgentSectionGenerator constructed.");
    }

    public async Task<string> GenerateSectionAsync(
        string consultDraft,
        string sectionName,
        string sectionStandard,
        CancellationToken cancellationToken)
    {
        return await SendPromptAsync(
            $"section:{sectionName}",
            BuildUserMessage(consultDraft, sectionName, sectionStandard),
            cancellationToken);
    }

    public async Task<string> GenerateSectionAsync(
        string consultDraft,
        IReadOnlyList<ClinicalConcept> patientTrajectory,
        string sectionName,
        string sectionStandard,
        CancellationToken cancellationToken)
    {
        return await SendPromptAsync(
            $"section:{sectionName}",
            BuildTrajectorySectionMessage(consultDraft, patientTrajectory, sectionName, sectionStandard),
            cancellationToken);
    }

    public async Task<string> GenerateStandardSectionDraftAsync(
        IReadOnlyList<ClinicalConcept> patientTrajectory,
        string sectionName,
        CancellationToken cancellationToken)
    {
        return await SendPromptAsync(
            $"section-standard-draft:{sectionName}",
            BuildStandardSectionDraftMessage(patientTrajectory, sectionName),
            cancellationToken);
    }

    public async Task<string> UpdateSectionWithPatientInformationAsync(
        string standardSectionDraft,
        string consultDraft,
        string sectionName,
        CancellationToken cancellationToken)
    {
        return await SendPromptAsync(
            $"section-patient-draft:{sectionName}",
            BuildPatientSectionDraftMessage(standardSectionDraft, consultDraft, sectionName),
            cancellationToken);
    }

    public async Task<string> ApplySectionInstructionsAsync(
        string patientSectionDraft,
        string sectionName,
        string sectionStandard,
        CancellationToken cancellationToken)
    {
        return await SendPromptAsync(
            $"section-instructions-applied:{sectionName}",
            BuildSectionInstructionsMessage(patientSectionDraft, sectionName, sectionStandard),
            cancellationToken);
    }

    public async Task<string> SendPromptAsync(
        string stage,
        string userMessage,
        CancellationToken cancellationToken)
    {
        var endpoint = Environment.GetEnvironmentVariable("AzureAI__Endpoint");
        var agentName = Environment.GetEnvironmentVariable("AzureAI__AgentName");
        var agentVersion = Environment.GetEnvironmentVariable("AzureAI__AgentVersion");
        var azureClientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
        var isRunningInAzure = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID"));
        var networkTimeoutSeconds = GetEnvironmentInt("AzureAI__NetworkTimeoutSeconds", 270);
        var maxRetries = GetEnvironmentInt("AzureAI__MaxRetries", 0);

        if (isRunningInAzure && string.IsNullOrWhiteSpace(azureClientId))
        {
            _logger.LogError(
                "AZURE_CLIENT_ID is missing while running in Azure. Configure it so DefaultAzureCredential uses the attached user-assigned managed identity.");

            throw new InvalidOperationException(
                "AZURE_CLIENT_ID must be set when running in Azure so DefaultAzureCredential uses the attached user-assigned managed identity.");
        }

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

        var projectClient = new AIProjectClient(endpointUri, _credential, projectClientOptions);

        ResponseResult sdkResponse;
        Console.Error.WriteLine($"[AgentPrompt] Stage={stage}; PromptLength={userMessage.Length}; Prompt={userMessage}");

        try
        {
            _logger.LogInformation(
                "Sending Foundry agent request. Stage={Stage}, AgentName={AgentName}, AgentVersion={AgentVersion}, MessageLength={MessageLength}",
                stage,
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
                "Foundry agent request completed with version. Stage={Stage}, ElapsedMs={ElapsedMs}",
                stage,
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
                "Foundry agent request completed without version. Stage={Stage}, ElapsedMs={ElapsedMs}",
                stage,
                retryStopwatch.ElapsedMilliseconds);
        }

        var assistantText = sdkResponse.GetOutputText();

        if (string.IsNullOrWhiteSpace(assistantText))
        {
            _logger.LogError("Foundry response did not contain assistant text. Stage={Stage}", stage);
            throw new InvalidOperationException("No assistant response found");
        }

        Console.Error.WriteLine($"[AgentResponse] Stage={stage}; ResponseLength={assistantText.Length}; Response={assistantText}");
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

    private static string BuildTrajectorySectionMessage(
        string consultDraft,
        IReadOnlyList<ClinicalConcept> patientTrajectory,
        string sectionName,
        string sectionStandard)
    {
        return $"""
            You are writing one section of an oncology consult note.

            Source of truth:
            Use only the clinical facts contained in the original draft consult note below.

            Organizing context:
            The structured patient trajectory below has already been reconciled from validated SNOMED concepts. Use it only to organize the section. Do not add typical trajectory details unless they are supported by the original draft consult note or by the validated patient trajectory.

            Section workflow:
            1. Generate a standard draft for the requested section.
            2. Update the section with patient information from the original draft.
            3. Apply the section standard and any section-specific user instructions.

            Missing information rule:
            Do not invent missing pathology, dates, staging, receptor status, genomic scores, medications, allergies, physical exam findings, or treatment decisions.
            If a detail is not present in the draft, omit it unless the section would be misleading without it, in which case write "not documented."

            Output rule:
            Return only the final prose for the requested section. Do not include a heading, JSON, markdown, bullets, or commentary.

            Requested section:
            {sectionName}

            Section standard and section-specific instructions:
            {sectionStandard}

            Validated patient trajectory:
            {FormatConcepts(patientTrajectory)}

            Original draft consult note:
            {consultDraft}
            """;
    }

    private static string BuildStandardSectionDraftMessage(
        IReadOnlyList<ClinicalConcept> patientTrajectory,
        string sectionName)
    {
        return $"""
            You are writing one standard section of an oncology consult note.

            Task:
            Write a standard draft for the requested section using the validated patient trajectory as organizing context.

            Guardrail:
            Do not add typical trajectory details unless they are present in the validated patient trajectory. This is a draft to be reconciled with the original consult note in the next step.

            Output rule:
            Return only prose for the requested section. Do not include a heading, JSON, markdown, bullets, or commentary.

            Requested section:
            {sectionName}

            Validated patient trajectory:
            {FormatConcepts(patientTrajectory)}
            """;
    }

    private static string BuildPatientSectionDraftMessage(
        string standardSectionDraft,
        string consultDraft,
        string sectionName)
    {
        return $"""
            You are updating one oncology consult note section with patient information.

            Source of truth:
            Use only the clinical facts contained in the original draft consult note below.

            Task:
            Rewrite the standard section draft so it reflects the patient information in the original draft consult note.

            Missing information rule:
            Do not invent missing pathology, dates, staging, receptor status, genomic scores, medications, allergies, physical exam findings, or treatment decisions.
            If a detail is not present in the original draft, omit it unless the section would be misleading without it, in which case write "not documented."

            Output rule:
            Return only prose for the requested section. Do not include a heading, JSON, markdown, bullets, or commentary.

            Requested section:
            {sectionName}

            Standard section draft:
            {standardSectionDraft}

            Original draft consult note:
            {consultDraft}
            """;
    }

    private static string BuildSectionInstructionsMessage(
        string patientSectionDraft,
        string sectionName,
        string sectionStandard)
    {
        var browserInstructions = string.IsNullOrWhiteSpace(sectionStandard)
            ? "No changes. Preserve the patient-updated draft's clinical content and produce polished final prose for this section."
            : sectionStandard;

        return $"""
            You are applying section-specific writing standards and user instructions to one oncology consult note section.

            Task:
            Revise the patient-updated section draft to follow the section standard and section-specific user instructions.

            Guardrail:
            Preserve the clinical facts already present in the patient-updated draft. Do not add new clinical facts from the standard or instructions.

            Output rule:
            Return only final prose for the requested section. Do not include a heading, JSON, markdown, bullets, or commentary.

            Requested section:
            {sectionName}

            Patient-updated section draft:
            {patientSectionDraft}

            Section standard and section-specific instructions:
            {browserInstructions}
            """;
    }

    private static string FormatConcepts(IReadOnlyList<ClinicalConcept> concepts)
    {
        if (concepts.Count == 0)
        {
            return "(none)";
        }

        return string.Join(
            Environment.NewLine,
            concepts.Select(concept =>
            {
                var support = string.IsNullOrWhiteSpace(concept.Support) ? string.Empty : $" support: {concept.Support}";
                return $"- {concept.Term} ({concept.Type}) - {concept.Id}; active: {concept.IsActive}; source: {concept.Source}{support}";
            }));
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
