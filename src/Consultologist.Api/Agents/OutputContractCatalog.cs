using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Consultologist.Api.Workflow;

namespace Consultologist.Api.Agents;

/// <summary>
/// Well-known output-contract ids. The ids name engine-recognized output shapes;
/// packages reference shapes by declaring canonically-matching schemas, never by
/// naming agents (docs/customizable-workflow/output-contract-catalog.md).
/// </summary>
public static class OutputContracts
{
    /// <summary>The prose default: nodes without an output schema.</summary>
    public const string Text = "text";

    /// <summary>The concept-list shape ClinicalConcept consumption is keyed to.</summary>
    public const string ConceptList = "concept-list";
}

/// <summary>One catalog entry: the attested agent pinned for an output shape.</summary>
public sealed record OutputContractEntry(
    string ContractId,
    string AgentName,
    string AgentVersion,
    string? SchemaJson);

/// <summary>
/// The output-contract catalog: the engine-side registry mapping schema ids to
/// attested agent pins. Loaded once at startup from the bundled, git-tracked
/// agents/output-contracts.json — the package declares the contract, the catalog
/// declares the executor, and startup attestation verifies every entry.
/// </summary>
public sealed class OutputContractCatalog
{
    private readonly Dictionary<string, OutputContractEntry> _entries;

    private OutputContractCatalog(Dictionary<string, OutputContractEntry> entries)
    {
        _entries = entries;
    }

    public IReadOnlyDictionary<string, OutputContractEntry> Entries => _entries;

    /// <summary>
    /// Resolves a contract id to its agent pin. Unknown ids are configuration errors
    /// (fail fast, non-retryable), not anomalies: the validator guarantees package
    /// schemas match catalog entries before any node runs.
    /// </summary>
    public OutputContractEntry GetEntry(string contractId)
    {
        if (!_entries.TryGetValue(contractId, out var entry))
        {
            throw new InvalidOperationException(
                $"Output contract '{contractId}' is not in the catalog. Known contracts: {string.Join(", ", _entries.Keys)}.");
        }

        return entry;
    }

    /// <summary>
    /// Finds the catalog entry whose schema canonically matches (sorted keys,
    /// title/description stripped — the validator's identity rules).
    /// </summary>
    public bool TryResolveContract(JsonNode? schema, out string contractId)
    {
        var canonical = WorkflowPackageValidator.CanonicalizeSchema(schema);

        foreach (var entry in _entries.Values)
        {
            if (entry.SchemaJson is not null
                && WorkflowPackageValidator.CanonicalizeSchema(JsonNode.Parse(entry.SchemaJson)) == canonical)
            {
                contractId = entry.ContractId;
                return true;
            }
        }

        contractId = string.Empty;
        return false;
    }

    /// <summary>
    /// The directory holding output-contracts.json and the agent manifests: the same
    /// AgentAttestation__ManifestDirectory convention the attestation service uses.
    /// </summary>
    public static string ResolveDirectory()
        => Environment.GetEnvironmentVariable("AgentAttestation__ManifestDirectory")
            ?? Path.Combine(AppContext.BaseDirectory, "agents");

    /// <summary>
    /// Loads and validates the catalog. Any defect is a startup failure: an engine
    /// without a coherent catalog must not accept jobs.
    /// </summary>
    public static OutputContractCatalog Load(string? directory = null)
    {
        directory ??= ResolveDirectory();
        var catalogPath = Path.Combine(directory, "output-contracts.json");

        if (!File.Exists(catalogPath))
        {
            throw new InvalidOperationException($"Output-contract catalog not found at '{catalogPath}'.");
        }

        CatalogFile? file;
        try
        {
            file = JsonSerializer.Deserialize<CatalogFile>(File.ReadAllText(catalogPath));
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Output-contract catalog at '{catalogPath}' is not valid JSON: {ex.Message}", ex);
        }

        if (file?.Contracts is null || file.Contracts.Count == 0)
        {
            throw new InvalidOperationException($"Output-contract catalog at '{catalogPath}' declares no contracts.");
        }

        var entries = new Dictionary<string, OutputContractEntry>(StringComparer.Ordinal);

        foreach (var (contractId, dto) in file.Contracts)
        {
            if (string.IsNullOrWhiteSpace(contractId)
                || string.IsNullOrWhiteSpace(dto.AgentName)
                || string.IsNullOrWhiteSpace(dto.AgentVersion))
            {
                throw new InvalidOperationException(
                    $"Output-contract catalog entry '{contractId}' must declare agentName and agentVersion.");
            }

            string? schemaJson = null;

            if (!string.IsNullOrWhiteSpace(dto.SchemaFile))
            {
                var schemaPath = Path.Combine(directory, dto.SchemaFile);

                if (!File.Exists(schemaPath))
                {
                    throw new InvalidOperationException(
                        $"Output-contract catalog entry '{contractId}' references missing schema file '{schemaPath}'.");
                }

                schemaJson = File.ReadAllText(schemaPath);

                try
                {
                    _ = JsonNode.Parse(schemaJson);
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException(
                        $"Schema file '{schemaPath}' for output contract '{contractId}' is not valid JSON: {ex.Message}", ex);
                }
            }

            entries[contractId] = new OutputContractEntry(contractId, dto.AgentName!, dto.AgentVersion!, schemaJson);
        }

        if (!entries.ContainsKey(OutputContracts.Text))
        {
            throw new InvalidOperationException(
                $"Output-contract catalog at '{catalogPath}' must declare the '{OutputContracts.Text}' entry (the prose default).");
        }

        return new OutputContractCatalog(entries);
    }

    private sealed record CatalogFile(
        [property: JsonPropertyName("contracts")] Dictionary<string, CatalogEntryDto>? Contracts);

    private sealed record CatalogEntryDto(
        [property: JsonPropertyName("agentName")] string? AgentName,
        [property: JsonPropertyName("agentVersion")] string? AgentVersion,
        [property: JsonPropertyName("schemaFile")] string? SchemaFile);
}
