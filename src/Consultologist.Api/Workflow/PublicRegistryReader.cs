using System.Text.Json;
using Azure.Storage.Blobs;
using Consultologist.Api.Agents;
using Microsoft.Extensions.Configuration;

namespace Consultologist.Api.Workflow;

public sealed record PublicChainResponse(
    IReadOnlyList<PublicPackageSummary> Packages,
    PublicCatalogSummary? OutputContracts,
    IReadOnlyList<PublicAgentSummary> AgentDefinitions,
    DateTimeOffset GeneratedAtUtc);

public sealed record PublicPackageSummary(string Name, string? Latest, IReadOnlyList<string> Versions);

public sealed record PublicCatalogSummary(
    string? Latest,
    IReadOnlyList<string> Versions,
    IReadOnlyDictionary<string, PublicContractSummary>? Contracts);

public sealed record PublicContractSummary(string AgentName, string AgentVersion, bool HasSchema);

public sealed record PublicAgentSummary(string Name, string? Latest, IReadOnlyList<string> Versions);

/// <summary>
/// The read side of the anonymous Public/Chain view (#95). This class knows
/// ONLY the public blob service URI — it holds no credential and no private
/// container client, so serving acct-* content is impossible by construction,
/// not by filtering: the private registry is simply unreachable from here.
/// </summary>
public sealed class PublicRegistryReader
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly BlobServiceClient? _service;

    public PublicRegistryReader(IConfiguration configuration)
    {
        var publicUri = configuration["WorkflowPackages:PublicBlobServiceUri"];
        _service = string.IsNullOrWhiteSpace(publicUri) ? null : new BlobServiceClient(new Uri(publicUri));
    }

    public bool IsConfigured => _service != null;

    public async Task<PublicChainResponse> BuildChainAsync(CancellationToken cancellationToken)
    {
        if (_service is null)
        {
            throw new InvalidOperationException("The public registry is not configured (WorkflowPackages__PublicBlobServiceUri).");
        }

        var packageNames = await ListBlobNamesAsync(WorkflowPackageBlobContainerFactory.ContainerName, cancellationToken);
        var contractNames = await ListBlobNamesAsync(OutputContractCatalogContainer, cancellationToken);
        var agentNames = await ListBlobNamesAsync(AgentDefinitionsContainer, cancellationToken);

        var smallFiles = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pointer in packageNames.Where(n => n.EndsWith("latest.json", StringComparison.Ordinal)))
        {
            smallFiles[$"{WorkflowPackageBlobContainerFactory.ContainerName}/{pointer}"] =
                await ReadTextAsync(WorkflowPackageBlobContainerFactory.ContainerName, pointer, cancellationToken);
        }

        foreach (var pointer in contractNames.Where(n => n.EndsWith("latest.json", StringComparison.Ordinal)))
        {
            smallFiles[$"{OutputContractCatalogContainer}/{pointer}"] =
                await ReadTextAsync(OutputContractCatalogContainer, pointer, cancellationToken);
        }

        foreach (var pointer in agentNames.Where(n => n.EndsWith("latest.json", StringComparison.Ordinal)))
        {
            smallFiles[$"{AgentDefinitionsContainer}/{pointer}"] =
                await ReadTextAsync(AgentDefinitionsContainer, pointer, cancellationToken);
        }

        // The latest catalog document itself, for the contract table.
        var catalogLatest = ReadPointer(smallFiles.GetValueOrDefault($"{OutputContractCatalogContainer}/latest.json"));
        if (catalogLatest != null && contractNames.Contains($"{catalogLatest}/{OutputContractCatalog.CatalogFileName}"))
        {
            smallFiles[$"{OutputContractCatalogContainer}/{catalogLatest}/{OutputContractCatalog.CatalogFileName}"] =
                await ReadTextAsync(OutputContractCatalogContainer, $"{catalogLatest}/{OutputContractCatalog.CatalogFileName}", cancellationToken);
        }

        return Assemble(packageNames, contractNames, agentNames, smallFiles, DateTimeOffset.UtcNow);
    }

    public const string OutputContractCatalogContainer = OutputContractCatalog.RegistryName;
    public const string AgentDefinitionsContainer = "agent-definitions";

    /// <summary>Pure aggregation over the blob listings and the pre-fetched small files — the unit-tested core.</summary>
    internal static PublicChainResponse Assemble(
        IReadOnlyList<string> packageBlobs,
        IReadOnlyList<string> contractBlobs,
        IReadOnlyList<string> agentBlobs,
        IReadOnlyDictionary<string, string> smallFiles,
        DateTimeOffset nowUtc)
    {
        // Packages: {name}/{version}/manifest.json + {name}/latest.json.
        var packages = packageBlobs
            .Select(n => n.Split('/'))
            .Where(parts => parts.Length == 3 && parts[2] == "manifest.json")
            .GroupBy(parts => parts[0], StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new PublicPackageSummary(
                g.Key,
                ReadPointer(smallFiles.GetValueOrDefault($"{WorkflowPackageBlobContainerFactory.ContainerName}/{g.Key}/latest.json")),
                SortVersions(g.Select(parts => parts[1]))))
            .ToList();

        // Catalog: {version}/output-contracts.json + latest.json.
        var catalogVersions = SortVersions(contractBlobs
            .Select(n => n.Split('/'))
            .Where(parts => parts.Length == 2 && parts[1] == OutputContractCatalog.CatalogFileName)
            .Select(parts => parts[0]));

        var catalogLatest = ReadPointer(smallFiles.GetValueOrDefault($"{OutputContractCatalogContainer}/latest.json"));
        IReadOnlyDictionary<string, PublicContractSummary>? contracts = null;

        var latestCatalogJson = catalogLatest is null
            ? null
            : smallFiles.GetValueOrDefault($"{OutputContractCatalogContainer}/{catalogLatest}/{OutputContractCatalog.CatalogFileName}");

        if (latestCatalogJson != null)
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<CatalogDocument>(latestCatalogJson, JsonOptions);
                contracts = parsed?.Contracts?.ToDictionary(
                    pair => pair.Key,
                    pair => new PublicContractSummary(
                        pair.Value.AgentName ?? string.Empty,
                        pair.Value.AgentVersion ?? string.Empty,
                        !string.IsNullOrWhiteSpace(pair.Value.SchemaFile)),
                    StringComparer.Ordinal);
            }
            catch (JsonException)
            {
                // The chain view is best-effort presentation; the loader/attestation
                // own catalog integrity.
            }
        }

        var catalog = catalogVersions.Count > 0 || catalogLatest != null
            ? new PublicCatalogSummary(catalogLatest, catalogVersions, contracts)
            : null;

        // Agent definitions: {name}/{version}/definition.yaml + {name}/latest.json.
        var agents = agentBlobs
            .Select(n => n.Split('/'))
            .Where(parts => parts.Length == 3 && parts[2] == "definition.yaml")
            .GroupBy(parts => parts[0], StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new PublicAgentSummary(
                g.Key,
                ReadPointer(smallFiles.GetValueOrDefault($"{AgentDefinitionsContainer}/{g.Key}/latest.json")),
                g.Select(parts => parts[1]).OrderBy(v => v.Length).ThenBy(v => v, StringComparer.Ordinal).ToList()))
            .ToList();

        return new PublicChainResponse(packages, catalog, agents, nowUtc);
    }

    private static List<string> SortVersions(IEnumerable<string> versions) =>
        versions
            .Distinct(StringComparer.Ordinal)
            .OrderBy(v => CalVerVersion.TryParse(v, out var parsed) ? parsed : default)
            .ToList();

    private static string? ReadPointer(string? pointerJson)
    {
        if (string.IsNullOrWhiteSpace(pointerJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LatestPointer>(pointerJson, JsonOptions)?.Version;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<List<string>> ListBlobNamesAsync(string containerName, CancellationToken cancellationToken)
    {
        var names = new List<string>();
        var container = _service!.GetBlobContainerClient(containerName);

        await foreach (var blob in container.GetBlobsAsync(cancellationToken: cancellationToken))
        {
            names.Add(blob.Name);
        }

        return names;
    }

    private async Task<string> ReadTextAsync(string containerName, string blobPath, CancellationToken cancellationToken)
    {
        var response = await _service!.GetBlobContainerClient(containerName).GetBlobClient(blobPath).DownloadContentAsync(cancellationToken);
        return response.Value.Content.ToString();
    }

    private sealed record LatestPointer(string? Version);

    private sealed record CatalogDocument(Dictionary<string, CatalogDocumentEntry>? Contracts);

    private sealed record CatalogDocumentEntry(string? AgentName, string? AgentVersion, string? SchemaFile);
}
