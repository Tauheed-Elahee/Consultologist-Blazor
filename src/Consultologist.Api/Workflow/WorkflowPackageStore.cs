using System.Collections.Concurrent;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Consultologist.Api.Agents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Consultologist.Api.Workflow;

public interface IWorkflowPackageStore
{
    Task<WorkflowPackage> ResolveAsync(WorkflowPackageRef packageRef, CancellationToken cancellationToken);
}

public sealed class WorkflowPackageStore : IWorkflowPackageStore
{
    private const string ContainerName = "workflow-packages";
    public const int SupportedSpecVersion = 5;
    private static readonly TimeSpan LatestPointerCacheDuration = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly BlobContainerClient _container;
    private readonly OutputContractCatalog _catalog;
    private readonly ILogger<WorkflowPackageStore> _logger;

    // Published package versions are immutable, so resolved packages cache forever;
    // only the mutable latest-pointers expire.
    private readonly ConcurrentDictionary<string, WorkflowPackage> _packageCache = new();
    private readonly ConcurrentDictionary<string, (string Version, DateTimeOffset FetchedAt)> _latestCache = new();

    public WorkflowPackageStore(
        IConfiguration configuration,
        TokenCredential credential,
        OutputContractCatalog catalog,
        ILogger<WorkflowPackageStore> logger)
    {
        _catalog = catalog;
        _logger = logger;

        // Entra ID first: when a blob service URI is configured, authenticate with the
        // app's managed identity (requires the Storage Blob Data Reader RBAC role on the
        // storage account). The connection-string path remains only as the local-dev
        // fallback (Azurite has no Entra endpoint).
        var serviceUri = configuration["WorkflowPackages:BlobServiceUri"];
        if (!string.IsNullOrWhiteSpace(serviceUri))
        {
            _container = new BlobServiceClient(new Uri(serviceUri), credential).GetBlobContainerClient(ContainerName);
            _logger.LogInformation("Workflow package store using Entra ID auth. BlobServiceUri={BlobServiceUri}", serviceUri);
            return;
        }

        var connectionStringName = configuration["WorkflowPackages:ConnectionStringName"] ?? "AzureWebJobsStorage";
        var connectionString = configuration[connectionStringName]
            ?? Environment.GetEnvironmentVariable(connectionStringName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Workflow package storage is not configured: set WorkflowPackages__BlobServiceUri (Entra ID) or {connectionStringName} (local dev).");
        }

        _container = new BlobContainerClient(connectionString, ContainerName);
        _logger.LogWarning("Workflow package store using connection-string auth (local-dev fallback). Prefer WorkflowPackages__BlobServiceUri with managed identity.");
    }

    public async Task<WorkflowPackage> ResolveAsync(WorkflowPackageRef packageRef, CancellationToken cancellationToken)
    {
        var version = packageRef.IsLatest
            ? await ResolveLatestVersionAsync(packageRef.Name, cancellationToken)
            : packageRef.Version;

        var cacheKey = $"{packageRef.Name}@{version}";
        if (_packageCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var manifestJson = await DownloadTextAsync($"{packageRef.Name}/{version}/manifest.json", cancellationToken);
        var manifest = JsonSerializer.Deserialize<WorkflowPackageManifest>(manifestJson, JsonOptions)
            ?? throw new InvalidOperationException($"Workflow package manifest for {cacheKey} is empty or malformed.");

        if (manifest.SpecVersion > SupportedSpecVersion)
        {
            throw new InvalidOperationException(
                $"Workflow package {cacheKey} requires spec version {manifest.SpecVersion}, but this runtime supports up to {SupportedSpecVersion}.");
        }

        // v5 packages have no standards.md — standards live in the data table
        // (package-format-v5.md); earlier spec versions require it.
        var standards = manifest.SpecVersion >= 5
            ? string.Empty
            : await DownloadTextAsync($"{packageRef.Name}/{version}/standards.md", cancellationToken);

        var loaded = manifest.SpecVersion >= 2
            ? await LoadPromptsAsync(packageRef.Name, version, manifest, cancellationToken)
            : default;
        var prompts = loaded.Prompts;

        // Every spec version resolves to one node DAG and one section-step list, so a
        // single interpreter path serves them all: v5 declares one-kind forEach nodes
        // natively (no section-step lowering — the interpreter slice retires it); v4
        // declares nodes and its map body lowers to section steps for run-prose-step;
        // v3 declares steps and synthesizes the canonical DAG; v2 synthesizes both.
        var sectionSteps = manifest.SpecVersion switch
        {
            >= 5 => null,
            4 => WorkflowNodeDefaults.LowerMapSteps(
                manifest.Nodes!.Single(n => string.Equals(n.Kind, WorkflowNodeKinds.Map, StringComparison.Ordinal))),
            3 => manifest.SectionSteps,
            2 => WorkflowSectionStepDefaults.V2Synthesized,
            _ => null
        };

        var nodes = manifest.SpecVersion switch
        {
            >= 4 => manifest.Nodes,
            >= 2 => WorkflowNodeDefaults.V3SynthesizedDag(sectionSteps!),
            _ => null
        };

        // Package schema ids resolve to catalog contract ids here, while the schema
        // file contents are in hand: v4 by canonical match, synthesized DAGs directly
        // (their nodes are built against the engine's concept-list contract).
        var schemaContracts = manifest.SpecVersion switch
        {
            >= 4 => loaded.SchemaContracts,
            >= 2 => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [WorkflowNodeDefaults.ConceptListSchemaId] = OutputContracts.ConceptList
            },
            _ => null
        };

        var package = new WorkflowPackage(manifest, standards, prompts, sectionSteps, nodes, schemaContracts, loaded.Data);

        _packageCache.TryAdd(cacheKey, package);
        _logger.LogInformation("Workflow package resolved. Package={Package}, SpecVersion={SpecVersion}, Prompts={PromptCount}", cacheKey, manifest.SpecVersion, prompts?.Count ?? 0);
        return package;
    }

    /// <summary>
    /// Downloads and validates the files of a specVersion-2+ package, and resolves
    /// declared schemas to catalog contract ids. Data gathering is two-stage: the
    /// manifest's data table names scalar files and collection index.json files, and
    /// each index names its item files. Missing data blobs are omitted (the validator
    /// reports them coherently); everything else fails loud on 404. Validation
    /// failures throw — the engine's fail-loud enforcement point.
    /// </summary>
    private async Task<(Dictionary<string, WorkflowPromptTemplate> Prompts, Dictionary<string, string> SchemaContracts, WorkflowPackageData? Data)> LoadPromptsAsync(
        string name,
        string version,
        WorkflowPackageManifest manifest,
        CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var paths = (manifest.Prompts ?? new List<WorkflowPromptSpec>()).Select(p => p.File)
            .Concat((manifest.Preludes ?? new Dictionary<string, string>()).Values)
            .Concat((manifest.Schemas ?? new Dictionary<string, string>()).Values)
            .Distinct(StringComparer.Ordinal);

        foreach (var path in paths)
        {
            files[path] = await DownloadTextAsync($"{name}/{version}/{path}", cancellationToken);
        }

        await GatherDataFilesAsync(name, version, manifest, files, cancellationToken);

        var catalogSchemas = _catalog.Entries.Values
            .Where(entry => entry.SchemaJson != null)
            .ToDictionary(entry => entry.ContractId, entry => entry.SchemaJson!, StringComparer.Ordinal);

        var result = WorkflowPackageValidator.Validate(manifest, files, catalogSchemas);

        foreach (var warning in result.Warnings)
        {
            _logger.LogWarning("Workflow package {Name}@{Version}: {Warning}", name, version, warning);
        }

        if (!result.IsValid)
        {
            throw new InvalidOperationException(
                $"Workflow package {name}@{version} failed specVersion-{manifest.SpecVersion} validation: {string.Join(" | ", result.Errors)}");
        }

        var prompts = manifest.Prompts!.ToDictionary(
            prompt => prompt.Id,
            prompt => new WorkflowPromptTemplate(
                prompt.Id,
                files[prompt.File],
                prompt.Variables,
                prompt.Prelude is null ? null : files[manifest.Preludes![prompt.Prelude]]),
            StringComparer.Ordinal);

        var schemaContracts = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (schemaId, path) in manifest.Schemas ?? new Dictionary<string, string>())
        {
            if (!_catalog.TryResolveContract(System.Text.Json.Nodes.JsonNode.Parse(files[path]), out var contractId))
            {
                throw new InvalidOperationException(
                    $"Workflow package {name}@{version} schema '{schemaId}' does not canonically match any catalog output contract.");
            }

            schemaContracts[schemaId] = contractId;
        }

        // Post-validation resolve: the validator has already guaranteed integrity,
        // so this collects no errors.
        var data = manifest.SpecVersion >= 5
            ? WorkflowDataResolver.Resolve(manifest, files, new List<string>())
            : null;

        return (prompts, schemaContracts, data);
    }

    /// <summary>
    /// Stage one: the data table's scalar files and collection indexes; stage two:
    /// each parseable index's item files. Unparseable indexes and missing blobs are
    /// left to the validator.
    /// </summary>
    private async Task GatherDataFilesAsync(
        string name,
        string version,
        WorkflowPackageManifest manifest,
        Dictionary<string, string> files,
        CancellationToken cancellationToken)
    {
        foreach (var (_, path) in manifest.Data ?? new Dictionary<string, string>())
        {
            if (!path.EndsWith('/'))
            {
                await TryAddBlobAsync(files, name, version, path, cancellationToken);
                continue;
            }

            var indexPath = path + WorkflowDataResolver.IndexFileName;

            if (!await TryAddBlobAsync(files, name, version, indexPath, cancellationToken))
            {
                continue;
            }

            WorkflowDataIndexFile? index;
            try
            {
                index = JsonSerializer.Deserialize<WorkflowDataIndexFile>(files[indexPath], JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            foreach (var item in index?.Items ?? new List<WorkflowDataIndexItem>())
            {
                if (!string.IsNullOrWhiteSpace(item.File))
                {
                    await TryAddBlobAsync(files, name, version, path + item.File, cancellationToken);
                }
            }
        }
    }

    private async Task<bool> TryAddBlobAsync(
        Dictionary<string, string> files,
        string name,
        string version,
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            files[path] = await DownloadTextAsync($"{name}/{version}/{path}", cancellationToken);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<string> ResolveLatestVersionAsync(string name, CancellationToken cancellationToken)
    {
        if (_latestCache.TryGetValue(name, out var cached)
            && DateTimeOffset.UtcNow - cached.FetchedAt < LatestPointerCacheDuration)
        {
            return cached.Version;
        }

        var pointerJson = await DownloadTextAsync($"{name}/latest.json", cancellationToken);
        var pointer = JsonSerializer.Deserialize<LatestPointer>(pointerJson, JsonOptions);

        if (pointer is null || !CalVerVersion.TryParse(pointer.Version, out _))
        {
            throw new InvalidOperationException($"Latest pointer for workflow package '{name}' is missing or holds an invalid version.");
        }

        _latestCache[name] = (pointer.Version, DateTimeOffset.UtcNow);
        return pointer.Version;
    }

    private async Task<string> DownloadTextAsync(string blobPath, CancellationToken cancellationToken)
    {
        try
        {
            var blob = _container.GetBlobClient(blobPath);
            var response = await blob.DownloadContentAsync(cancellationToken);
            return response.Value.Content.ToString();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            throw new InvalidOperationException($"Workflow package blob '{blobPath}' was not found in container '{ContainerName}'.", ex);
        }
    }

    private sealed record LatestPointer(string Version);
}
