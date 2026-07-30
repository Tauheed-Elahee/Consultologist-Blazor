using Consultologist.Api.Agents;
using Consultologist.Api.Documents;
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
    PackageNotExecutable,
    SpecVersionNotYetExecutable,
    InputsMismatch,
    // #238: a supplied document could not be read. Well-formed request,
    // unsatisfiable content — 422 like InputsMismatch, not 400.
    InputFileUnreadable
}

/// <summary>
/// The outcome of resolving a request's inputs against the package declaration:
/// Effective is the resolver map (every declared id present; absent optional
/// inputs as empty strings), Supplied the caller's map for hashing (absent
/// optional inputs omitted). Both null on the v5/v6 legacy path.
/// </summary>
internal sealed record EffectiveInputsResolution(
    IReadOnlyDictionary<string, string>? Effective,
    IReadOnlyDictionary<string, string>? Supplied,
    string? Error);

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

        // #238: documents become text before anything else looks at the
        // request, so everything downstream — validation, resolution, hashing,
        // the orchestration — stays the string-keyed pipeline it already was.
        // Extraction is the pre-step docs/DOCUMENT_INPUT.md describes, not a
        // new kind of input.
        var extraction = await ExtractInputFilesAsync(request, cancellationToken);
        if (extraction.Error != null)
        {
            _logger.LogWarning(
                "Rejected job start: an attached document could not be read. Outcome={Outcome}",
                extraction.Outcome);
            return new ConsultGenerationJobStartOutcome(
                null,
                ConsultGenerationJobStartError.InputFileUnreadable,
                extraction.Error);
        }

        request = NormalizeInputs(extraction.Request);
        var inputOrigins = extraction.Origins;

        var inputs = ResolveEffectiveInputs(request, package.Manifest);
        if (inputs.Error != null)
        {
            _logger.LogWarning(
                "Rejected job start: inputs do not satisfy the package declaration. Package={Package}, Detail={Detail}",
                package.Ref,
                inputs.Error);
            return new ConsultGenerationJobStartOutcome(
                null,
                ConsultGenerationJobStartError.InputsMismatch,
                inputs.Error);
        }

        // A consult_draft-only Inputs map against a legacy package folds into
        // the draft field, so everything downstream sees the v5/v6 shape.
        if (package.Manifest.SpecVersion < 7 && request.Inputs is { Count: > 0 })
        {
            request = request with { ConsultDraft = request.Inputs[ConsultDraftInputId], Inputs = null };
        }

        // A multi-deliverable v7 package resolves ResultNodeId null by design —
        // its result SET is the executability signal.
        if (package.Nodes is not { Count: > 0 }
            || (package.ResultNodeId is null && package.Results is not { Count: > 0 }))
        {
            _logger.LogWarning("Workflow package {Package} (specVersion {SpecVersion}) has no executable nodes; jobs require specVersion 2 or newer.", package.Ref, package.Manifest.SpecVersion);
            return new ConsultGenerationJobStartOutcome(
                null,
                ConsultGenerationJobStartError.PackageNotExecutable,
                $"Workflow package {package.Ref} (specVersion {package.Manifest.SpecVersion}) predates prompt templates; pin a specVersion 2 or newer package.");
        }

        // v5: the result node's collection is the one section source. v6/v7:
        // Items carries the deliverable BLOCKS (each result aggregator's
        // expansion — WorkflowPackageBlocks dispatches the id scheme by spec)
        // and Collections carries one item set per fanned collection
        // (package-format-v6-design.md §§ 4–5; package-format-v7.md).
        IReadOnlyList<IReadOnlyDictionary<string, string>> items;
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string>>>? collectionSets = null;

        if (package.Manifest.SpecVersion >= 6)
        {
            items = WorkflowPackageBlocks.Resolve(package)
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
        // v5/v6: the hash covers the draft only (definition version 2). v7: the
        // supplied input map (definition version 3 — absent optional inputs
        // omitted). Sections are package content, covered by the
        // workflowPackage ref; agent identities are covered by catalogRef — the
        // record stores refs, not copies (#105).
        // See docs/customizable-workflow/provenance.md.
        var isV7 = package.Manifest.SpecVersion >= 7;
        var effectiveInputHash = isV7
            ? ConsultGenerationProvenance.ComputeDeclaredInputsHash(inputs.Supplied!)
            : ConsultGenerationProvenance.ComputeDraftOnlyHash(request);
        var effectiveInputHashVersion = isV7
            ? ConsultGenerationProvenance.DeclaredInputsHashVersion
            : 2;
        var resultDescriptors = package.Results?
            .Select(result => new ConsultResultDescriptor(result.Id, result.NodeId, result.Label))
            .ToList();

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
                nodes,
                EffectiveInputHashVersion: effectiveInputHashVersion,
                Source: origin.Source,
                ScheduledAtUtc: request.ScheduledAtUtc,
                InputOrigins: inputOrigins));

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
                EffectiveInputHashVersion: effectiveInputHashVersion,
                CatalogRef: _catalog.ResolvedRef,
                Collections: collectionSets,
                Source: origin.Source,
                ReplyToAddress: origin.ReplyToAddress,
                Results: resultDescriptors,
                Inputs: inputs.Effective,
                InputOrigins: inputOrigins),
            new StartOrchestrationOptions { InstanceId = jobId },
            cancellationToken);

        _logger.LogInformation(
            "Consult generation job started. JobId={JobId}, Source={Source}, BlockCount={BlockCount}",
            instanceId,
            origin.Source,
            items.Count);

        return new ConsultGenerationJobStartOutcome(instanceId);
    }

    internal const string ConsultDraftInputId = "consult_draft";

    /// <summary>
    /// Resolves the request's inputs against the package declaration
    /// (package-format-v7.md request contract). v5/v6: only a
    /// consult_draft-only Inputs map is acceptable (folded into the draft by
    /// the caller); v7: the supplied map (or the back-filled legacy draft)
    /// must cover every required declared id and name no undeclared ones.
    /// </summary>
    /// <summary>
    /// The result of turning a request's attached documents into text: the
    /// request with those slots filled and <see cref="ConsultGenerationRequest.InputFiles"/>
    /// cleared, plus what the server observed about each one.
    /// </summary>
    internal sealed record InputFileExtraction(
        ConsultGenerationRequest Request,
        IReadOnlyDictionary<string, ConsultInputOrigin>? Origins,
        string? Error,
        string? Outcome);

    /// <summary>
    /// Reads every attached document and folds its text into the input map
    /// (#238). One refusal fails the whole start: a consult generated from
    /// the inputs that happened to be readable would be a partial referral
    /// presented as a whole one.
    /// </summary>
    internal static async Task<InputFileExtraction> ExtractInputFilesAsync(
        ConsultGenerationRequest request,
        CancellationToken cancellationToken)
    {
        if (request.InputFiles is not { Count: > 0 })
        {
            return new InputFileExtraction(request, null, null, null);
        }

        var inputs = request.Inputs is { Count: > 0 }
            ? new Dictionary<string, string>(request.Inputs, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var origins = new Dictionary<string, ConsultInputOrigin>(StringComparer.Ordinal);

        foreach (var (id, file) in request.InputFiles.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var result = await DocumentExtraction.ExtractAsync(file.Content, cancellationToken);

            if (!DocumentExtraction.Succeeded(result))
            {
                return new InputFileExtraction(
                    request,
                    null,
                    DocumentExtractionCopy.For(result.Outcome),
                    result.Outcome);
            }

            inputs[id] = result.Text!;
            origins[id] = new ConsultInputOrigin(
                ConsultInputOriginKinds.Document,
                result.ExtractorId,
                result.PageCount,
                result.TrackedChangesResolved);
        }

        // InputFiles cleared here, and this is load-bearing rather than tidy:
        // the request is carried verbatim into the orchestration input, which
        // Durable persists to the storage account and spills to blob past the
        // inline limit. Leaving the bytes on would put every attached document
        // at rest with no retention story, contradicting the promise that
        // extraction keeps them transient (docs/DOCUMENT_INPUT.md § 5).
        // Nothing downstream needs them: the text is in Inputs.
        return new InputFileExtraction(
            request with { Inputs = inputs, InputFiles = null },
            origins,
            null,
            null);
    }

    /// <summary>
    /// CRLF to LF, trailing whitespace off the end — applied to every input,
    /// typed and extracted alike, before the effective-input hash sees any of
    /// it (#238, docs/DOCUMENT_INPUT.md § 2).
    ///
    /// Nothing here normalised before, so the same referral pasted from a
    /// Windows editor and attached as a file hashed differently for no reason
    /// a reader of the record could see. Normalising only extracted text would
    /// have kept that split, which is the property this milestone exists to
    /// close. The hash *definition* is unchanged — only the text reaching it —
    /// so DeclaredInputsHashVersion stays 3.
    /// </summary>
    internal static ConsultGenerationRequest NormalizeInputs(ConsultGenerationRequest request)
    {
        var draft = Normalize(request.ConsultDraft);

        if (request.Inputs is not { Count: > 0 })
        {
            return request with { ConsultDraft = draft };
        }

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (id, value) in request.Inputs)
        {
            normalized[id] = Normalize(value) ?? string.Empty;
        }

        return request with { ConsultDraft = draft, Inputs = normalized };
    }

    private static string? Normalize(string? text) =>
        text?.Replace("\r\n", "\n").TrimEnd();

    internal static EffectiveInputsResolution ResolveEffectiveInputs(
        ConsultGenerationRequest request,
        WorkflowPackageManifest manifest)
    {
        if (manifest.SpecVersion < 7)
        {
            if (request.Inputs is { Count: > 0 })
            {
                var foreign = request.Inputs.Keys
                    .Where(id => !string.Equals(id, ConsultDraftInputId, StringComparison.Ordinal))
                    .Order(StringComparer.Ordinal)
                    .ToList();
                if (foreign.Count > 0)
                {
                    return new EffectiveInputsResolution(null, null,
                        $"Inputs names undeclared input(s) {string.Join(", ", foreign.Select(id => $"'{id}'"))}: a specVersion {manifest.SpecVersion} package accepts only consult_draft.");
                }
            }

            return new EffectiveInputsResolution(null, null, null);
        }

        var declared = manifest.Inputs ?? new List<WorkflowInputSpec>();
        var supplied = request.Inputs is { Count: > 0 }
            ? request.Inputs
            : !string.IsNullOrWhiteSpace(request.ConsultDraft)
                ? new Dictionary<string, string>(StringComparer.Ordinal) { [ConsultDraftInputId] = request.ConsultDraft }
                : null;

        if (supplied is null)
        {
            return new EffectiveInputsResolution(null, null, "No inputs were supplied.");
        }

        var declaredIds = declared.Select(input => input.Id).ToHashSet(StringComparer.Ordinal);
        var unknown = supplied.Keys
            .Where(id => !declaredIds.Contains(id))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (unknown.Count > 0)
        {
            return new EffectiveInputsResolution(null, null,
                $"Unknown input(s) {string.Join(", ", unknown.Select(id => $"'{id}'"))} (declared: {string.Join(", ", declaredIds.Order(StringComparer.Ordinal))}).");
        }

        var missing = declared
            .Where(input => input.Required
                && (!supplied.TryGetValue(input.Id, out var value) || string.IsNullOrWhiteSpace(value)))
            .Select(input => input.Id)
            .ToList();
        if (missing.Count > 0)
        {
            return new EffectiveInputsResolution(null, null,
                $"Required input(s) {string.Join(", ", missing.Select(id => $"'{id}'"))} missing.");
        }

        // The resolver map covers every declared id — an absent optional input
        // renders as empty (package-format-v7-design.md § 3 resolution rule).
        var effective = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var input in declared)
        {
            effective[input.Id] = supplied.GetValueOrDefault(input.Id, string.Empty);
        }

        return new EffectiveInputsResolution(effective, supplied, null);
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
