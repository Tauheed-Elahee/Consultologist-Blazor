using System.Collections.Concurrent;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
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
    public const int SupportedSpecVersion = 1;
    private static readonly TimeSpan LatestPointerCacheDuration = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly BlobContainerClient _container;
    private readonly ILogger<WorkflowPackageStore> _logger;

    // Published package versions are immutable, so resolved packages cache forever;
    // only the mutable latest-pointers expire.
    private readonly ConcurrentDictionary<string, WorkflowPackage> _packageCache = new();
    private readonly ConcurrentDictionary<string, (string Version, DateTimeOffset FetchedAt)> _latestCache = new();

    public WorkflowPackageStore(IConfiguration configuration, ILogger<WorkflowPackageStore> logger)
    {
        var connectionStringName = configuration["WorkflowPackages:ConnectionStringName"] ?? "AzureWebJobsStorage";
        var connectionString = configuration[connectionStringName]
            ?? Environment.GetEnvironmentVariable(connectionStringName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException($"{connectionStringName} is not configured for workflow package storage.");
        }

        _container = new BlobContainerClient(connectionString, ContainerName);
        _logger = logger;
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

        var standards = await DownloadTextAsync($"{packageRef.Name}/{version}/standards.md", cancellationToken);
        var package = new WorkflowPackage(manifest, standards);

        _packageCache.TryAdd(cacheKey, package);
        _logger.LogInformation("Workflow package resolved. Package={Package}, SpecVersion={SpecVersion}", cacheKey, manifest.SpecVersion);
        return package;
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
