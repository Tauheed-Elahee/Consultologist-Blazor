using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Azure.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Consultologist.Api.Agents;

/// <summary>
/// Startup check that the deployed Foundry agent version matches the git-attested
/// manifest bundled with the app (agents/{name}.yaml). Git is the source of truth;
/// portal-side drift is logged loudly, or fails the host when
/// AgentAttestation__Enforce=true. See docs/customizable-workflow/provenance.md.
/// </summary>
public sealed class AgentAttestationService : IHostedService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TokenCredential _credential;
    private readonly ILogger<AgentAttestationService> _logger;

    public AgentAttestationService(
        IHttpClientFactory httpClientFactory,
        TokenCredential credential,
        ILogger<AgentAttestationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _credential = credential;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var enforce = string.Equals(
            Environment.GetEnvironmentVariable("AgentAttestation__Enforce"), "true", StringComparison.OrdinalIgnoreCase);

        try
        {
            var mismatches = await RunAsync(cancellationToken);

            if (mismatches == null)
            {
                return; // not configured for this environment; already logged
            }

            if (mismatches.Count == 0)
            {
                return;
            }

            var summary = string.Join("; ", mismatches);
            _logger.LogError("Agent attestation FAILED — deployed agent drifts from the git manifest: {Mismatches}", summary);
            Console.Error.WriteLine($"[AgentAttestation] FAILED: {summary}");

            if (enforce)
            {
                throw new InvalidOperationException($"Agent attestation failed: {summary}");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException || !enforce)
        {
            // The check must not take the app down on transient errors unless enforcing.
            _logger.LogWarning(ex, "Agent attestation could not run.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task<List<string>?> RunAsync(CancellationToken cancellationToken)
    {
        var endpoint = Environment.GetEnvironmentVariable("AzureAI__Endpoint");
        var agentName = Environment.GetEnvironmentVariable("AzureAI__AgentName");
        var agentVersion = Environment.GetEnvironmentVariable("AzureAI__AgentVersion");
        var apiVersion = Environment.GetEnvironmentVariable("AzureAI__ApiVersion") ?? "v1";

        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(agentName) || string.IsNullOrWhiteSpace(agentVersion))
        {
            _logger.LogInformation("Agent attestation skipped: AzureAI__Endpoint/AgentName/AgentVersion not configured.");
            return null;
        }

        var manifestPath = Environment.GetEnvironmentVariable("AgentAttestation__ManifestPath")
            ?? Path.Combine(AppContext.BaseDirectory, "agents", $"{agentName}.yaml");

        if (!File.Exists(manifestPath))
        {
            _logger.LogWarning("Agent attestation skipped: manifest not found at {ManifestPath}.", manifestPath);
            return null;
        }

        var manifest = AttestedAgentManifest.Load(await File.ReadAllTextAsync(manifestPath, cancellationToken));

        var mismatches = new List<string>();

        if (!string.Equals(manifest.Version, agentVersion, StringComparison.Ordinal))
        {
            mismatches.Add($"pinned version {agentVersion} != manifest version {manifest.Version}");
        }

        var deployed = await FetchDeployedDefinitionAsync(endpoint, agentName, agentVersion, apiVersion, cancellationToken);
        mismatches.AddRange(AttestedAgentManifest.Compare(manifest, deployed));

        if (mismatches.Count == 0)
        {
            _logger.LogInformation("Agent attestation passed. Agent={AgentName}@{AgentVersion}", agentName, agentVersion);
            Console.Error.WriteLine($"[AgentAttestation] passed: {agentName}@{agentVersion}");
        }

        return mismatches;
    }

    private async Task<JsonNode> FetchDeployedDefinitionAsync(
        string endpoint, string agentName, string agentVersion, string apiVersion, CancellationToken cancellationToken)
    {
        var token = await _credential.GetTokenAsync(
            new TokenRequestContext(new[] { "https://ai.azure.com/.default" }), cancellationToken);

        var url = $"{endpoint.TrimEnd('/')}/agents/{Uri.EscapeDataString(agentName)}/versions/{Uri.EscapeDataString(agentVersion)}?api-version={apiVersion}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonNode.Parse(body) ?? throw new InvalidOperationException("Foundry returned an empty agent definition.");
    }
}

public sealed class AttestedAgentManifest
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public AttestedAgentDefinition Definition { get; set; } = new();

    public sealed class AttestedAgentDefinition
    {
        public string Model { get; set; } = string.Empty;
        public string Instructions { get; set; } = string.Empty;
        public AttestedReasoning? Reasoning { get; set; }
        public string? ToolChoice { get; set; }
        public List<AttestedTool> Tools { get; set; } = new();
    }

    public sealed class AttestedReasoning
    {
        public string? Effort { get; set; }
    }

    public sealed class AttestedTool
    {
        public string Type { get; set; } = string.Empty;
        public string? ServerLabel { get; set; }
        public string? ServerUrl { get; set; }
    }

    public static AttestedAgentManifest Load(string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        return deserializer.Deserialize<AttestedAgentManifest>(yaml)
            ?? throw new InvalidOperationException("Agent manifest YAML is empty.");
    }

    /// <summary>
    /// Compares the manifest against the deployed agent-version JSON returned by the
    /// Foundry API. Returns one human-readable line per drifted field.
    /// </summary>
    public static List<string> Compare(AttestedAgentManifest manifest, JsonNode deployed)
    {
        var mismatches = new List<string>();
        var definition = deployed["definition"];

        if (definition == null)
        {
            mismatches.Add("deployed agent has no definition");
            return mismatches;
        }

        CompareValue(mismatches, "model", manifest.Definition.Model, definition["model"]?.ToString());
        CompareValue(mismatches, "tool_choice", manifest.Definition.ToolChoice, definition["tool_choice"]?.ToString());
        CompareValue(mismatches, "reasoning.effort", manifest.Definition.Reasoning?.Effort, definition["reasoning"]?["effort"]?.ToString());

        if (NormalizeText(manifest.Definition.Instructions) != NormalizeText(definition["instructions"]?.ToString()))
        {
            mismatches.Add("instructions differ");
        }

        var deployedTools = definition["tools"]?.AsArray() ?? new JsonArray();
        if (deployedTools.Count != manifest.Definition.Tools.Count)
        {
            mismatches.Add($"tools count: manifest {manifest.Definition.Tools.Count} != deployed {deployedTools.Count}");
            return mismatches;
        }

        for (var i = 0; i < deployedTools.Count; i++)
        {
            var expected = manifest.Definition.Tools[i];
            var actual = deployedTools[i];
            CompareValue(mismatches, $"tools[{i}].type", expected.Type, actual?["type"]?.ToString());
            CompareValue(mismatches, $"tools[{i}].server_label", expected.ServerLabel, actual?["server_label"]?.ToString());
            CompareValue(mismatches, $"tools[{i}].server_url", expected.ServerUrl, actual?["server_url"]?.ToString());
        }

        return mismatches;
    }

    private static void CompareValue(List<string> mismatches, string field, string? expected, string? actual)
    {
        if (!string.Equals(expected ?? string.Empty, actual ?? string.Empty, StringComparison.Ordinal))
        {
            mismatches.Add($"{field}: manifest '{expected}' != deployed '{actual}'");
        }
    }

    // Line endings and trailing whitespace differ between YAML block scalars and the
    // API's JSON strings without changing agent behavior.
    private static string NormalizeText(string? text) =>
        (text ?? string.Empty).Replace("\r\n", "\n").TrimEnd();
}
