using Consultologist.Api.Agents;
using Consultologist.Api.Models;
using Consultologist.Api.Workflow;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Entities;
using Microsoft.Extensions.Logging;

namespace Consultologist.Api.Jobs;

public static class ConsultGenerationJobSources
{
    public const string App = "app";
    public const string Email = "email";
}

public sealed record ConsultGenerationJobOrigin(
    string Source,
    string? ReplyToAddress = null);

public enum ConsultGenerationJobStartError
{
    MalformedPackageRef,
    ForeignPackageRef,
    RegistryUnavailable,
    PackageNotExecutable
}

public sealed record ConsultGenerationJobStartOutcome(
    string? JobId,
    ConsultGenerationJobStartError? Error = null,
    string? ErrorDetail = null);

public interface IConsultGenerationJobStarter
{
    /// <summary>
    /// Starts a consult-generation job for an already-resolved app user. The
    /// request must have passed ValidateRequest. Callers: the HTTP endpoint
    /// (bearer-authed) and the email-intake poller (sender-matched).
    /// </summary>
    Task<ConsultGenerationJobStartOutcome> StartAsync(
        DurableTaskClient client,
        ConsultGenerationRequest request,
        string appUserId,
        ConsultGenerationJobOrigin origin,
        CancellationToken cancellationToken);
}

public sealed class ConsultGenerationJobStarter : IConsultGenerationJobStarter
{
    private readonly ILogger<ConsultGenerationJobStarter> _logger;
    private readonly IWorkflowPackageStore _packageStore;
    private readonly IWorkflowPackagePinResolver _pinResolver;
    private readonly OutputContractCatalog _catalog;

    public ConsultGenerationJobStarter(
        ILogger<ConsultGenerationJobStarter> logger,
        IWorkflowPackageStore packageStore,
        IWorkflowPackagePinResolver pinResolver,
        OutputContractCatalog catalog)
    {
        _logger = logger;
        _packageStore = packageStore;
        _pinResolver = pinResolver;
        _catalog = catalog;
    }

    public async Task<ConsultGenerationJobStartOutcome> StartAsync(
        DurableTaskClient client,
        ConsultGenerationRequest request,
        string appUserId,
        ConsultGenerationJobOrigin origin,
        CancellationToken cancellationToken)
    {
        var jobId = Guid.NewGuid().ToString("N");
        var entityId = new EntityInstanceId(nameof(ConsultGenerationJobEntity), jobId);

        // A workflow package is mandatory: resolve the ref here (request → account
        // pin → default) to a concrete immutable version and snapshot it into the
        // job, so the whole run — and the provenance record — uses one version even
        // when the pin says "latest". Registry failure stops the job before it exists.
        if (!WorkflowPackageRef.TryParse(request.WorkflowPackage, out var packageRef))
        {
            if (!string.IsNullOrWhiteSpace(request.WorkflowPackage))
            {
                _logger.LogWarning("Invalid consult job request: malformed workflow package ref '{PackageRef}'.", request.WorkflowPackage);
                return new ConsultGenerationJobStartOutcome(
                    null,
                    ConsultGenerationJobStartError.MalformedPackageRef,
                    "WorkflowPackage is not a valid package reference.");
            }

            packageRef = await _pinResolver.ResolvePinAsync(appUserId, cancellationToken);
        }
        else if (!WorkflowPackageNaming.CanAccess(packageRef!.Name, appUserId))
        {
            // The acct-* access rule at job start: a caller-supplied ref to a
            // foreign account package is rejected before any registry read.
            _logger.LogWarning(
                "Rejected foreign account-package ref at job start. AppUserId={AppUserId}, Ref={Ref}",
                appUserId,
                request.WorkflowPackage);
            return new ConsultGenerationJobStartOutcome(
                null,
                ConsultGenerationJobStartError.ForeignPackageRef,
                "Workflow package is not accessible from this account.");
        }

        WorkflowPackage package;
        try
        {
            package = await _packageStore.ResolveAsync(packageRef!, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Workflow package resolution failed at job start. Pin={Pin}", packageRef);
            return new ConsultGenerationJobStartOutcome(
                null,
                ConsultGenerationJobStartError.RegistryUnavailable,
                "Workflow package registry is unavailable.");
        }

        if (package.Nodes is not { Count: > 0 } || package.ResultNodeId is null)
        {
            _logger.LogWarning("Workflow package {Package} (specVersion {SpecVersion}) has no executable nodes; jobs require specVersion 2 or newer.", package.Ref, package.Manifest.SpecVersion);
            return new ConsultGenerationJobStartOutcome(
                null,
                ConsultGenerationJobStartError.PackageNotExecutable,
                $"Workflow package {package.Ref} (specVersion {package.Manifest.SpecVersion}) predates prompt templates; pin a specVersion 2 or newer package.");
        }

        // v5: the result node's collection is the one section source. v6: Items
        // carries the deliverable's BLOCKS (the result aggregator's expansion)
        // and Collections carries one item set per fanned collection
        // (package-format-v6-design.md §§ 4–5).
        IReadOnlyList<IReadOnlyDictionary<string, string>> items;
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string>>>? collectionSets = null;

        if (package.Manifest.SpecVersion == 6)
        {
            items = WorkflowPackageBlocks.ResolveBlocks(package)
                .Select(block => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["id"] = block.Id,
                    ["name"] = block.Name
                })
                .ToList();

            collectionSets = package.Nodes
                .Where(node => node.ForEach != null)
                .Select(node => node.ForEach![WorkflowNodeBindingSources.DataPrefix.Length..])
                .Distinct(StringComparer.Ordinal)
                .ToDictionary(
                    collectionId => collectionId,
                    collectionId => (IReadOnlyList<IReadOnlyDictionary<string, string>>)(package.Data?.Collections.GetValueOrDefault(collectionId)
                            ?? throw new InvalidOperationException($"Package {package.Ref} has no data collection '{collectionId}'."))
                        .Items
                        .Select(item => (IReadOnlyDictionary<string, string>)item.Fields)
                        .ToList(),
                    StringComparer.Ordinal);
        }
        else
        {
            var collection = WorkflowPackageBlocks.ResolveCollection(package);
            items = collection.Items
                .Select(item => (IReadOnlyDictionary<string, string>)item.Fields)
                .ToList();
        }

        var dataScalars = package.Data?.Scalars;

        var resolvedPackageRef = package.Ref;
        // The per-section step list is the forEach chain, in manifest order — the
        // display/progress skeleton the section-prose-step events hang off.
        var sectionSteps = package.Nodes
            .Where(node => node.ForEach != null)
            .Select(node => new ConsultItemStepDescriptor(node.Id, node.Label))
            .ToList();
        var nodes = package.Nodes.Select(node => DescribeNode(node, package.SchemaContracts)).ToList();

        // Provenance: identify the artifacts and input that produce this consult.
        // The hash covers the draft only (definition version 2); sections are
        // package content, covered by the workflowPackage ref; agent identities
        // are covered by catalogRef — the record stores refs, not copies (#105).
        // See docs/customizable-workflow/provenance.md.
        var effectiveInputHash = ConsultGenerationProvenance.ComputeDraftOnlyHash(request);

        await client.Entities.SignalEntityAsync(
            entityId,
            nameof(ConsultGenerationJobEntity.Initialize),
            new ConsultGenerationJobInitialize(
                jobId,
                appUserId,
                items,
                resolvedPackageRef,
                effectiveInputHash,
                sectionSteps,
                nodes));

        var instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
            nameof(ConsultGenerationOrchestrator),
            new ConsultGenerationOrchestrationInput(
                request,
                appUserId,
                resolvedPackageRef,
                effectiveInputHash,
                sectionSteps,
                nodes,
                package.ResultNodeId,
                items,
                dataScalars,
                CatalogRef: _catalog.ResolvedRef,
                Collections: collectionSets),
            new StartOrchestrationOptions { InstanceId = jobId },
            cancellationToken);

        _logger.LogInformation(
            "Consult generation job started. JobId={JobId}, Source={Source}, BlockCount={BlockCount}",
            instanceId,
            origin.Source,
            items.Count);

        return new ConsultGenerationJobStartOutcome(instanceId);
    }

    internal static ConsultNodeDescriptor DescribeNode(
        WorkflowNodeSpec node,
        IReadOnlyDictionary<string, string>? schemaContracts)
    {
        return new ConsultNodeDescriptor(
            node.Id,
            node.Label,
            node.Prompt,
            node.Bindings?.ToDictionary(
                pair => pair.Key,
                pair => new ConsultNodeBindingDescriptor(pair.Value.From, pair.Value.As),
                StringComparer.Ordinal),
            OutputContract: node.Output is null
                ? null
                : schemaContracts?.GetValueOrDefault(node.Output.Schema)
                    ?? throw new InvalidOperationException(
                        $"Node '{node.Id}' declares schema '{node.Output.Schema}' with no resolved output contract."),
            FailIfEmpty: node.Output?.FailIfEmpty,
            ForEach: node.ForEach,
            ConceptSource: WorkflowNodeDefaults.WellKnownConceptSources.GetValueOrDefault(node.Id, node.Id),
            Aggregate: node.Aggregate);
    }
}
