using Azure.Core;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Consultologist.Api.Workflow;

/// <summary>
/// Builds the workflow-package registry's container client, shared by the
/// read-side store and the registry writer. Entra ID first: when a blob service
/// URI is configured, authenticate with the app's managed identity (reads need
/// Storage Blob Data Reader; publishing needs Contributor). The
/// connection-string path remains only as the local-dev fallback (Azurite has
/// no Entra endpoint).
/// </summary>
public sealed class WorkflowPackageBlobContainerFactory
{
    public const string ContainerName = "workflow-packages";

    private readonly BlobContainerClient _container;

    public WorkflowPackageBlobContainerFactory(
        IConfiguration configuration,
        TokenCredential credential,
        ILogger<WorkflowPackageBlobContainerFactory> logger)
    {
        var serviceUri = configuration["WorkflowPackages:BlobServiceUri"];
        if (!string.IsNullOrWhiteSpace(serviceUri))
        {
            _container = new BlobServiceClient(new Uri(serviceUri), credential).GetBlobContainerClient(ContainerName);
            logger.LogInformation("Workflow package registry using Entra ID auth. BlobServiceUri={BlobServiceUri}", serviceUri);
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
        logger.LogWarning("Workflow package registry using connection-string auth (local-dev fallback). Prefer WorkflowPackages__BlobServiceUri with managed identity.");
    }

    public BlobContainerClient GetContainer() => _container;
}
