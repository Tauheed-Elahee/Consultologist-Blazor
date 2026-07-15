using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Consultologist.Api.Agents;

/// <summary>
/// Startup check over every output-contract catalog entry: the deployed Foundry agent
/// must match the git-attested manifest bundled with the app (agents/{name}.yaml), and
/// the catalog's declared schema must match the schema welded into that manifest. Git
/// is the source of truth; drift is logged loudly, or fails the host when
/// AgentAttestation__Enforce=true. See docs/customizable-workflow/provenance.md and
/// output-contract-catalog.md.
/// </summary>
public sealed class AgentAttestationService : IHostedService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TokenCredential _credential;
    private readonly OutputContractCatalog _catalog;
    private readonly ILogger<AgentAttestationService> _logger;

    public AgentAttestationService(
        IHttpClientFactory httpClientFactory,
        TokenCredential credential,
        OutputContractCatalog catalog,
        ILogger<AgentAttestationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _credential = credential;
        _catalog = catalog;
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
        var apiVersion = Environment.GetEnvironmentVariable("AzureAI__ApiVersion") ?? "v1";

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            _logger.LogInformation("Agent attestation skipped: AzureAI__Endpoint not configured.");
            return null;
        }

        var mismatches = new List<string>();

        // Every catalog entry's agent gets attested against its git manifest, plus the
        // catalog↔manifest schema cross-check: an entry whose declared schema drifts
        // from the schema welded into its agent definition is a config defect, not a
        // deployment drift, but fails the same way — the catalog must never promise a
        // shape its agent doesn't enforce.
        foreach (var entry in _catalog.Entries.Values)
        {
            var manifestPath = Path.Combine(OutputContractCatalog.ResolveDirectory(), $"{entry.AgentName}.yaml");

            if (!File.Exists(manifestPath))
            {
                mismatches.Add($"{entry.AgentName}@{entry.AgentVersion}: catalog contract '{entry.ContractId}' has no git manifest at {manifestPath}");
                continue;
            }

            var manifest = AttestedAgentManifest.Load(await File.ReadAllTextAsync(manifestPath, cancellationToken));
            var agentMismatches = new List<string>();

            if (!string.Equals(manifest.Version, entry.AgentVersion, StringComparison.Ordinal))
            {
                agentMismatches.Add($"pinned version {entry.AgentVersion} != manifest version {manifest.Version}");
            }

            agentMismatches.AddRange(CompareCatalogSchema(entry, manifest));

            var deployed = await FetchDeployedDefinitionAsync(endpoint, entry.AgentName, entry.AgentVersion, apiVersion, cancellationToken);
            agentMismatches.AddRange(AttestedAgentManifest.Compare(manifest, deployed));

            if (agentMismatches.Count == 0)
            {
                _logger.LogInformation(
                    "Agent attestation passed. Contract={ContractId}, Agent={AgentName}@{AgentVersion}",
                    entry.ContractId,
                    entry.AgentName,
                    entry.AgentVersion);
                Console.Error.WriteLine($"[AgentAttestation] passed: {entry.ContractId} -> {entry.AgentName}@{entry.AgentVersion}");
            }

            mismatches.AddRange(agentMismatches.Select(m => $"{entry.AgentName}@{entry.AgentVersion}: {m}"));
        }

        return mismatches;
    }

    /// <summary>
    /// The git-side half of the attestation: the catalog's schema and the manifest's
    /// published text.format must agree before either is compared to the deployment.
    /// </summary>
    internal static List<string> CompareCatalogSchema(OutputContractEntry entry, AttestedAgentManifest manifest)
    {
        var mismatches = new List<string>();
        var format = manifest.Definition.Text?.Format;

        if (entry.SchemaJson is null)
        {
            if (string.Equals(format?.Type, "json_schema", StringComparison.Ordinal))
            {
                mismatches.Add($"catalog contract '{entry.ContractId}' declares no schema but the manifest publishes a json_schema text format");
            }

            return mismatches;
        }

        if (!string.Equals(format?.Type, "json_schema", StringComparison.Ordinal))
        {
            mismatches.Add($"catalog contract '{entry.ContractId}' declares a schema but the manifest text.format.type is '{format?.Type ?? "(none)"}'");
            return mismatches;
        }

        if (AttestedAgentManifest.CanonicalJson(JsonNode.Parse(entry.SchemaJson))
            != AttestedAgentManifest.CanonicalJson(string.IsNullOrWhiteSpace(format.Schema) ? null : JsonNode.Parse(format.Schema)))
        {
            mismatches.Add($"catalog contract '{entry.ContractId}' schema differs from the manifest text.format.schema");
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
        public AttestedText? Text { get; set; }
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

    public sealed class AttestedText
    {
        public AttestedTextFormat? Format { get; set; }
    }

    public sealed class AttestedTextFormat
    {
        public string Type { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string? Strict { get; set; }

        /// <summary>JSON document as a string (YAML block scalar); compared canonically.</summary>
        public string? Schema { get; set; }
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

        // The text format is part of the agent's behavioral contract — for the
        // structured-output agent it carries the response schema itself.
        var manifestFormat = manifest.Definition.Text?.Format;
        var deployedFormat = definition["text"]?["format"];
        CompareValue(mismatches, "text.format.type", manifestFormat?.Type, deployedFormat?["type"]?.ToString());
        CompareValue(mismatches, "text.format.name", manifestFormat?.Name, deployedFormat?["name"]?.ToString());
        CompareValue(mismatches, "text.format.strict", manifestFormat?.Strict?.ToLowerInvariant(), deployedFormat?["strict"]?.ToString().ToLowerInvariant());

        var manifestSchema = string.IsNullOrWhiteSpace(manifestFormat?.Schema) ? null : JsonNode.Parse(manifestFormat.Schema);
        var deployedSchema = deployedFormat?["schema"];
        if (CanonicalJson(manifestSchema) != CanonicalJson(deployedSchema))
        {
            mismatches.Add("text.format.schema differs");
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

    /// <summary>Order-insensitive JSON equality: objects re-serialized with sorted keys.</summary>
    internal static string CanonicalJson(JsonNode? node)
    {
        if (node is null)
        {
            return "null";
        }

        if (node is JsonObject obj)
        {
            var parts = obj
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{JsonSerializer.Serialize(pair.Key)}:{CanonicalJson(pair.Value)}");
            return "{" + string.Join(",", parts) + "}";
        }

        if (node is JsonArray array)
        {
            return "[" + string.Join(",", array.Select(CanonicalJson)) + "]";
        }

        return node.ToJsonString();
    }
}
