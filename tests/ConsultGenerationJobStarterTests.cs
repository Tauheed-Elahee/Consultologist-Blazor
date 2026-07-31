using System.Text;
using Consultologist.Api.Documents;
using Consultologist.Api.Jobs;
using Consultologist.Api.Models;
using Consultologist.Api.Workflow;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Client.Entities;
using Microsoft.DurableTask.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Consultologist.Api.Tests;

public class ConsultGenerationJobStarterTests
{
    private readonly IWorkflowPackageStore _packageStore = Substitute.For<IWorkflowPackageStore>();
    private readonly IWorkflowPackagePinResolver _pinResolver = Substitute.For<IWorkflowPackagePinResolver>();
    private readonly DurableTaskClient _client = Substitute.For<DurableTaskClient>("test");
    private readonly DurableEntityClient _entities = Substitute.For<DurableEntityClient>("test");

    private ConsultGenerationJobStarter CreateStarter(ILogger<ConsultGenerationJobStarter>? logger = null)
    {
        _client.Entities.Returns(_entities);

        return new ConsultGenerationJobStarter(
            logger ?? NullLogger<ConsultGenerationJobStarter>.Instance,
            _packageStore,
            _pinResolver,
            TestCatalog.Instance);
    }

    private static WorkflowPackage ExecutableV5Package()
    {
        var manifest = V5Fixtures.Manifest();
        var files = V5Fixtures.Files(manifest);
        var errors = new List<string>();
        var data = WorkflowDataResolver.Resolve(manifest, files, errors);
        Assert.Empty(errors);

        return new WorkflowPackage(
            manifest,
            Nodes: manifest.Nodes,
            SchemaContracts: TestOutputContracts.CatalogSchemas,
            Data: data,
            ResultNodeId: "section-instructions");
    }

    [Fact]
    public async Task MalformedPackageRef_ReturnsError()
    {
        var outcome = await CreateStarter().StartAsync(
            _client,
            new ConsultGenerationRequest("draft", "not a valid ref"),
            "user-1",
            new ConsultGenerationJobOrigin(ConsultGenerationJobSources.App),
            CancellationToken.None);

        Assert.Equal(ConsultGenerationJobStartError.MalformedPackageRef, outcome.Error);
        Assert.Null(outcome.JobId);
    }

    [Fact]
    public async Task ForeignAccountPackageRef_ReturnsError()
    {
        var outcome = await CreateStarter().StartAsync(
            _client,
            new ConsultGenerationRequest("draft", "acct-deadbeefdead@latest"),
            "11112222333344445555666677778888",
            new ConsultGenerationJobOrigin(ConsultGenerationJobSources.App),
            CancellationToken.None);

        Assert.Equal(ConsultGenerationJobStartError.ForeignPackageRef, outcome.Error);
    }

    [Fact]
    public async Task RegistryFailure_ReturnsError()
    {
        _pinResolver.ResolvePinAsync("user-1", Arg.Any<CancellationToken>())
            .Returns(new WorkflowPackageRef("general", "latest"));
        _packageStore.ResolveAsync(Arg.Any<WorkflowPackageRef>(), Arg.Any<CancellationToken>())
            .Returns<Task<WorkflowPackage>>(_ => throw new InvalidOperationException("registry down"));

        var outcome = await CreateStarter().StartAsync(
            _client,
            new ConsultGenerationRequest("draft"),
            "user-1",
            new ConsultGenerationJobOrigin(ConsultGenerationJobSources.Email, "user@example.com"),
            CancellationToken.None);

        Assert.Equal(ConsultGenerationJobStartError.RegistryUnavailable, outcome.Error);
    }

    [Fact]
    public async Task NonExecutablePackage_ReturnsError()
    {
        _pinResolver.ResolvePinAsync("user-1", Arg.Any<CancellationToken>())
            .Returns(new WorkflowPackageRef("general", "latest"));
        _packageStore.ResolveAsync(Arg.Any<WorkflowPackageRef>(), Arg.Any<CancellationToken>())
            .Returns(new WorkflowPackage(V5Fixtures.Manifest()));

        var outcome = await CreateStarter().StartAsync(
            _client,
            new ConsultGenerationRequest("draft"),
            "user-1",
            new ConsultGenerationJobOrigin(ConsultGenerationJobSources.App),
            CancellationToken.None);

        Assert.Equal(ConsultGenerationJobStartError.PackageNotExecutable, outcome.Error);
    }

    private static WorkflowPackage ExecutableV7Package(
        WorkflowPackageManifest manifest,
        IReadOnlyList<WorkflowResolvedResult> results)
    {
        var files = V6Fixtures.Files(manifest);
        var errors = new List<string>();
        var data = WorkflowDataResolver.Resolve(manifest, files, errors);
        Assert.Empty(errors);

        return new WorkflowPackage(
            manifest,
            Nodes: manifest.Nodes,
            SchemaContracts: TestOutputContracts.CatalogSchemas,
            Data: data,
            ResultNodeId: results.Count == 1 ? results[0].NodeId : null,
            Results: results);
    }

    [Fact]
    public async Task SpecVersion7Package_StartsWithPrefixedBlocksAndInputMap()
    {
        // The legacy draft field back-fills the consult_draft slot; the
        // snapshot carries prefixed block ids, the result set, the effective
        // input map, and hash version 3.
        var request = new ConsultGenerationRequest("The referral body");
        _pinResolver.ResolvePinAsync("user-1", Arg.Any<CancellationToken>())
            .Returns(new WorkflowPackageRef("general", "latest"));
        _packageStore.ResolveAsync(Arg.Any<WorkflowPackageRef>(), Arg.Any<CancellationToken>())
            .Returns(ExecutableV7Package(
                V7Fixtures.Minimal(),
                new List<WorkflowResolvedResult> { new("consult", "assemble-note", "Assemble note") }));

        ConsultGenerationJobInitialize? initialize = null;
        await _entities.SignalEntityAsync(
            Arg.Any<EntityInstanceId>(),
            nameof(ConsultGenerationJobEntity.Initialize),
            Arg.Do<object>(payload => initialize = payload as ConsultGenerationJobInitialize));

        ConsultGenerationOrchestrationInput? orchestrationInput = null;
        _client.ScheduleNewOrchestrationInstanceAsync(
                Arg.Any<TaskName>(),
                Arg.Do<object?>(payload => orchestrationInput = payload as ConsultGenerationOrchestrationInput),
                Arg.Any<StartOrchestrationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(((StartOrchestrationOptions?)callInfo[2])!.InstanceId!));

        var outcome = await CreateStarter().StartAsync(
            _client,
            request,
            "user-1",
            new ConsultGenerationJobOrigin(ConsultGenerationJobSources.App),
            CancellationToken.None);

        Assert.Null(outcome.Error);
        Assert.NotNull(initialize);
        Assert.Equal(
            new[] { "consult:section-instructions:hpi", "consult:section-instructions:pmh" },
            initialize.Items.Select(item => item["id"]).ToArray());
        Assert.Equal(3, initialize.EffectiveInputHashVersion);
        Assert.NotNull(orchestrationInput);
        Assert.Equal(3, orchestrationInput.EffectiveInputHashVersion);
        Assert.Equal(
            ConsultGenerationProvenance.ComputeDeclaredInputsHash(
                new Dictionary<string, string> { ["consult_draft"] = "The referral body" }),
            orchestrationInput.EffectiveInputHash);
        Assert.Equal(
            new[] { new ConsultResultDescriptor("consult", "assemble-note", "Assemble note") },
            orchestrationInput.Results!.ToArray());
        Assert.Equal(
            new Dictionary<string, string> { ["consult_draft"] = "The referral body" },
            orchestrationInput.Inputs);
    }

    [Fact]
    public async Task SpecVersion7MultiResultPackage_StartsWithBothDeliverables()
    {
        // ResultNodeId is null by design for a multi-result set — the result
        // set itself is the executability signal.
        var request = new ConsultGenerationRequest(
            null,
            Inputs: new Dictionary<string, string> { ["consult_draft"] = "The referral body" });
        _pinResolver.ResolvePinAsync("user-1", Arg.Any<CancellationToken>())
            .Returns(new WorkflowPackageRef("general", "latest"));
        _packageStore.ResolveAsync(Arg.Any<WorkflowPackageRef>(), Arg.Any<CancellationToken>())
            .Returns(ExecutableV7Package(
                V7Fixtures.MultiDeliverable(),
                new List<WorkflowResolvedResult>
                {
                    new("consult_note", "assemble-note", "Consultation note"),
                    new("patient_letter", "assemble-letter", "Patient letter")
                }));

        ConsultGenerationOrchestrationInput? orchestrationInput = null;
        _client.ScheduleNewOrchestrationInstanceAsync(
                Arg.Any<TaskName>(),
                Arg.Do<object?>(payload => orchestrationInput = payload as ConsultGenerationOrchestrationInput),
                Arg.Any<StartOrchestrationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(((StartOrchestrationOptions?)callInfo[2])!.InstanceId!));

        var outcome = await CreateStarter().StartAsync(
            _client,
            request,
            "user-1",
            new ConsultGenerationJobOrigin(ConsultGenerationJobSources.App),
            CancellationToken.None);

        Assert.Null(outcome.Error);
        Assert.NotNull(orchestrationInput);
        Assert.Null(orchestrationInput.ResultNodeId);
        Assert.Equal(2, orchestrationInput.Results!.Count);
        // The optional prior_notes input was not supplied: the effective map
        // carries it empty for the resolver; the hash covers the supplied map.
        Assert.Equal(string.Empty, orchestrationInput.Inputs!["prior_notes"]);
        Assert.Equal(
            ConsultGenerationProvenance.ComputeDeclaredInputsHash(
                new Dictionary<string, string> { ["consult_draft"] = "The referral body" }),
            orchestrationInput.EffectiveInputHash);
    }

    [Fact]
    public async Task SpecVersion7Package_UnknownInput_ReturnsInputsMismatch()
    {
        _pinResolver.ResolvePinAsync("user-1", Arg.Any<CancellationToken>())
            .Returns(new WorkflowPackageRef("general", "latest"));
        _packageStore.ResolveAsync(Arg.Any<WorkflowPackageRef>(), Arg.Any<CancellationToken>())
            .Returns(ExecutableV7Package(
                V7Fixtures.Minimal(),
                new List<WorkflowResolvedResult> { new("consult", "assemble-note", "Assemble note") }));

        var outcome = await CreateStarter().StartAsync(
            _client,
            new ConsultGenerationRequest(null, Inputs: new Dictionary<string, string> { ["labs"] = "CBC normal." }),
            "user-1",
            new ConsultGenerationJobOrigin(ConsultGenerationJobSources.App),
            CancellationToken.None);

        Assert.Equal(ConsultGenerationJobStartError.InputsMismatch, outcome.Error);
        Assert.Contains("'labs'", outcome.ErrorDetail);
        Assert.Contains("declared: consult_draft", outcome.ErrorDetail);
    }

    [Fact]
    public async Task Success_SignalsInitializeAndSchedulesWithSameJobIdAndDraftHash()
    {
        var request = new ConsultGenerationRequest("The referral body");
        _pinResolver.ResolvePinAsync("user-1", Arg.Any<CancellationToken>())
            .Returns(new WorkflowPackageRef("general", "latest"));
        _packageStore.ResolveAsync(Arg.Any<WorkflowPackageRef>(), Arg.Any<CancellationToken>())
            .Returns(ExecutableV5Package());

        ConsultGenerationJobInitialize? initialize = null;
        EntityInstanceId? entityId = null;
        await _entities.SignalEntityAsync(
            Arg.Do<EntityInstanceId>(id => entityId = id),
            nameof(ConsultGenerationJobEntity.Initialize),
            Arg.Do<object>(payload => initialize = payload as ConsultGenerationJobInitialize));

        ConsultGenerationOrchestrationInput? orchestrationInput = null;
        StartOrchestrationOptions? options = null;
        _client.ScheduleNewOrchestrationInstanceAsync(
                Arg.Any<TaskName>(),
                Arg.Do<object?>(payload => orchestrationInput = payload as ConsultGenerationOrchestrationInput),
                Arg.Do<StartOrchestrationOptions?>(o => options = o),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(((StartOrchestrationOptions?)callInfo[2])!.InstanceId!));

        var outcome = await CreateStarter().StartAsync(
            _client,
            request,
            "user-1",
            new ConsultGenerationJobOrigin(ConsultGenerationJobSources.App),
            CancellationToken.None);

        Assert.Null(outcome.Error);
        Assert.NotNull(outcome.JobId);
        Assert.NotNull(initialize);
        Assert.Equal(outcome.JobId, initialize.JobId);
        Assert.Equal(outcome.JobId, options?.InstanceId);
        // EntityInstanceId normalizes entity names to lowercase.
        Assert.Equal(nameof(ConsultGenerationJobEntity), entityId?.Name, ignoreCase: true);
        Assert.Equal("user-1", initialize.AppUserId);
        Assert.NotNull(orchestrationInput);
        Assert.Equal(
            ConsultGenerationProvenance.ComputeDraftOnlyHash(request),
            orchestrationInput.EffectiveInputHash);
        Assert.Equal(initialize.EffectiveInputHash, orchestrationInput.EffectiveInputHash);
    }
    [Fact]
    public async Task AttachedDocument_FillsItsSlotAndHashesLikeTheEquivalentText()
    {
        // The check that proves extraction stayed a pre-step: a slot filled
        // from a document and the same slot typed by hand are the same input,
        // and the record says so by producing the same hash.
        var request = new ConsultGenerationRequest(
            null,
            InputFiles: new Dictionary<string, InputFilePayload>
            {
                ["consult_draft"] = new("text/plain", "The referral body"u8.ToArray())
            });

        var captured = await StartV7AndCaptureAsync(request);

        Assert.Null(captured.Outcome.Error);
        Assert.Equal(
            new Dictionary<string, string> { ["consult_draft"] = "The referral body" },
            captured.OrchestrationInput!.Inputs);
        Assert.Equal(
            ConsultGenerationProvenance.ComputeDeclaredInputsHash(
                new Dictionary<string, string> { ["consult_draft"] = "The referral body" }),
            captured.OrchestrationInput.EffectiveInputHash);
    }

    [Fact]
    public async Task AttachedDocument_IsRecordedAsReadFromADocument()
    {
        var request = new ConsultGenerationRequest(
            null,
            InputFiles: new Dictionary<string, InputFilePayload>
            {
                ["consult_draft"] = new("text/plain", "The referral body"u8.ToArray())
            });

        var captured = await StartV7AndCaptureAsync(request);

        var origin = Assert.Contains("consult_draft", captured.Initialize!.InputOrigins);
        Assert.Equal(ConsultInputOriginKinds.Document, origin.Kind);
        Assert.Equal("text/1", origin.Extractor);
    }

    [Fact]
    public async Task TypedInput_RecordsNoOrigin()
    {
        // Absence means "not recorded", never "typed" — email jobs supply text
        // until #237, and every job predating this field has none either.
        var request = new ConsultGenerationRequest(
            null,
            Inputs: new Dictionary<string, string> { ["consult_draft"] = "The referral body" });

        var captured = await StartV7AndCaptureAsync(request);

        Assert.Null(captured.Initialize!.InputOrigins);
        Assert.Null(captured.OrchestrationInput!.InputOrigins);
    }

    [Fact]
    public async Task AttachedDocumentBytes_NeverReachDurableState()
    {
        // The regression guard. The whole request is carried into the
        // orchestration input, which Durable persists to the storage account
        // and spills to blob past the inline limit — so leaving the bytes on
        // would put every attached document at rest with no retention story.
        // The extracted text is in Inputs; nothing downstream needs the file.
        var request = new ConsultGenerationRequest(
            null,
            InputFiles: new Dictionary<string, InputFilePayload>
            {
                ["consult_draft"] = new("text/plain", "The referral body"u8.ToArray())
            });

        var captured = await StartV7AndCaptureAsync(request);

        Assert.Null(captured.OrchestrationInput!.Request.InputFiles);
    }

    [Fact]
    public async Task UnreadableDocument_IsRefusedWithTheSameSentenceThePreviewGives()
    {
        // Binary that is not a format we read. It must come back as a start
        // error rather than an exception, and say the same thing the preview
        // endpoint would have said about the same bytes.
        var request = new ConsultGenerationRequest(
            null,
            InputFiles: new Dictionary<string, InputFilePayload>
            {
                ["consult_draft"] = new("application/octet-stream", [0x00, 0x01, 0x02, 0x00, 0xFF])
            });

        var captured = await StartV7AndCaptureAsync(request);

        Assert.Equal(ConsultGenerationJobStartError.InputFileUnreadable, captured.Outcome.Error);
        Assert.Equal(
            DocumentExtractionCopy.For(DocumentExtractionOutcomes.UnsupportedType),
            captured.Outcome.ErrorDetail);
    }

    [Fact]
    public void CrlfAndLfText_NowHashIdentically()
    {
        // Nothing normalised before, so the same referral pasted from a
        // Windows editor and typed on Linux were "different input" to the
        // record for a reason no reader of it could see.
        var windows = ConsultGenerationJobStarter.NormalizeInputs(new ConsultGenerationRequest(
            null,
            Inputs: new Dictionary<string, string> { ["consult_draft"] = "One.\r\nTwo.\r\n" }));
        var unix = ConsultGenerationJobStarter.NormalizeInputs(new ConsultGenerationRequest(
            null,
            Inputs: new Dictionary<string, string> { ["consult_draft"] = "One.\nTwo." }));

        Assert.Equal(
            ConsultGenerationProvenance.ComputeDeclaredInputsHash(windows.Inputs!),
            ConsultGenerationProvenance.ComputeDeclaredInputsHash(unix.Inputs!));
    }

    [Fact]
    public void BareCrText_HashesLikeItsLfEquivalent()
    {
        // The CRLF case above was closed in #238; a lone \r survived both
        // normalisation sites until #251, so § 2's "conservative and
        // universal" was broader than the code. A referral carrying classic
        // Mac endings hashed differently from the same referral typed, and
        // the record called them different input for a reason no reader
        // could see.
        var mac = ConsultGenerationJobStarter.NormalizeInputs(new ConsultGenerationRequest(
            null,
            Inputs: new Dictionary<string, string> { ["consult_draft"] = "One.\rTwo.\r" }));
        var unix = ConsultGenerationJobStarter.NormalizeInputs(new ConsultGenerationRequest(
            null,
            Inputs: new Dictionary<string, string> { ["consult_draft"] = "One.\nTwo." }));

        Assert.Equal(
            ConsultGenerationProvenance.ComputeDeclaredInputsHash(mac.Inputs!),
            ConsultGenerationProvenance.ComputeDeclaredInputsHash(unix.Inputs!));
    }

    [Fact]
    public void ANullConsultDraft_SurvivesNormalisationAsNull()
    {
        // The trap in sharing one normaliser across call sites: this runs
        // over ConsultDraft too, and a helper that collapsed null to ""
        // would turn a v5/v6 job's absent draft into an empty one. The v2
        // draft-only hash serialises the field, so {"consultDraft":null}
        // and {"consultDraft":""} are different hashes.
        var normalized = ConsultGenerationJobStarter.NormalizeInputs(
            new ConsultGenerationRequest(null, Inputs: null));

        Assert.Null(normalized.ConsultDraft);
    }

    private sealed record StartCapture(
        ConsultGenerationJobStartOutcome Outcome,
        ConsultGenerationJobInitialize? Initialize,
        ConsultGenerationOrchestrationInput? OrchestrationInput);

    // ---- the logging audit (#241, § 9) ----------------------------------
    //
    // "Bytes are never persisted and never logged, including on the exception
    // paths." Traced by reading every log statement on this path, and pinned
    // here so it is a property rather than an observation.

    private const string Sentinel = "SENTINEL-CLINICAL-CONTENT-0f1e2d";

    [Fact]
    public async Task AReadableDocument_PutsNoneOfItsContentInTheLog()
    {
        var log = new CapturingLogger<ConsultGenerationJobStarter>();

        await StartV7AndCaptureAsync(
            new ConsultGenerationRequest(
                null,
                InputFiles: new Dictionary<string, InputFilePayload>
                {
                    ["consult_draft"] = new("text/plain", Encoding.UTF8.GetBytes(Sentinel))
                }),
            log);

        Assert.DoesNotContain(Sentinel, log.Everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreadableDocument_PutsNoneOfItsContentInTheLog()
    {
        // The exception path is the one § 9 calls out, because it is where
        // "include the input so we can debug it" is most tempting. The bytes
        // here are a truncated zip carrying the sentinel, so a handler that
        // echoed any fragment of what it was reading would be caught.
        var log = new CapturingLogger<ConsultGenerationJobStarter>();
        var corrupt = Encoding.UTF8.GetBytes("PK" + Sentinel);

        var captured = await StartV7AndCaptureAsync(
            new ConsultGenerationRequest(
                null,
                InputFiles: new Dictionary<string, InputFilePayload>
                {
                    ["consult_draft"] = new("application/octet-stream", corrupt)
                }),
            log);

        Assert.Equal(ConsultGenerationJobStartError.InputFileUnreadable, captured.Outcome.Error);
        Assert.DoesNotContain(Sentinel, log.Everything, StringComparison.Ordinal);
    }

    private async Task<StartCapture> StartV7AndCaptureAsync(
        ConsultGenerationRequest request,
        ILogger<ConsultGenerationJobStarter>? logger = null)
    {
        _pinResolver.ResolvePinAsync("user-1", Arg.Any<CancellationToken>())
            .Returns(new WorkflowPackageRef("general", "latest"));
        _packageStore.ResolveAsync(Arg.Any<WorkflowPackageRef>(), Arg.Any<CancellationToken>())
            .Returns(ExecutableV7Package(
                V7Fixtures.Minimal(),
                new List<WorkflowResolvedResult> { new("consult", "assemble-note", "Assemble note") }));

        ConsultGenerationJobInitialize? initialize = null;
        await _entities.SignalEntityAsync(
            Arg.Any<EntityInstanceId>(),
            nameof(ConsultGenerationJobEntity.Initialize),
            Arg.Do<object>(payload => initialize = payload as ConsultGenerationJobInitialize));

        ConsultGenerationOrchestrationInput? orchestrationInput = null;
        _client.ScheduleNewOrchestrationInstanceAsync(
                Arg.Any<TaskName>(),
                Arg.Do<object?>(payload => orchestrationInput = payload as ConsultGenerationOrchestrationInput),
                Arg.Any<StartOrchestrationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(((StartOrchestrationOptions?)callInfo[2])!.InstanceId!));

        var outcome = await CreateStarter(logger).StartAsync(
            _client,
            request,
            "user-1",
            new ConsultGenerationJobOrigin(ConsultGenerationJobSources.App),
            CancellationToken.None);

        return new StartCapture(outcome, initialize, orchestrationInput);
    }

}

public class ResolveEffectiveInputsTests
{
    [Fact]
    public void LegacyPackage_DraftOnly_ResolvesNullMaps()
    {
        var resolution = ConsultGenerationJobStarter.ResolveEffectiveInputs(
            new ConsultGenerationRequest("Draft."), V5Fixtures.Manifest());

        Assert.Null(resolution.Error);
        Assert.Null(resolution.Effective);
        Assert.Null(resolution.Supplied);
    }

    [Fact]
    public void LegacyPackage_ForeignInputId_IsRejected()
    {
        var resolution = ConsultGenerationJobStarter.ResolveEffectiveInputs(
            new ConsultGenerationRequest(null, Inputs: new Dictionary<string, string>
            {
                ["consult_draft"] = "Draft.",
                ["labs"] = "CBC normal."
            }),
            V6Fixtures.SingleCollection());

        Assert.Contains("'labs'", resolution.Error);
        Assert.Contains("accepts only consult_draft", resolution.Error);
    }

    [Fact]
    public void V7_LegacyDraft_BackFillsTheConventionalSlot()
    {
        var resolution = ConsultGenerationJobStarter.ResolveEffectiveInputs(
            new ConsultGenerationRequest("Draft."), V7Fixtures.Minimal());

        Assert.Null(resolution.Error);
        Assert.Equal(new Dictionary<string, string> { ["consult_draft"] = "Draft." }, resolution.Supplied);
        Assert.Equal(new Dictionary<string, string> { ["consult_draft"] = "Draft." }, resolution.Effective);
    }

    [Fact]
    public void V7_AbsentOptionalInput_IsEmptyInEffectiveAndOmittedInSupplied()
    {
        var resolution = ConsultGenerationJobStarter.ResolveEffectiveInputs(
            new ConsultGenerationRequest(null, Inputs: new Dictionary<string, string> { ["consult_draft"] = "Draft." }),
            V7Fixtures.MultiDeliverable());

        Assert.Null(resolution.Error);
        Assert.False(resolution.Supplied!.ContainsKey("prior_notes"));
        Assert.Equal(string.Empty, resolution.Effective!["prior_notes"]);
        Assert.Equal("Draft.", resolution.Effective["consult_draft"]);
    }

    [Fact]
    public void V7_MissingRequiredInput_IsRejected()
    {
        var resolution = ConsultGenerationJobStarter.ResolveEffectiveInputs(
            new ConsultGenerationRequest(null, Inputs: new Dictionary<string, string> { ["prior_notes"] = "Old notes." }),
            V7Fixtures.MultiDeliverable());

        Assert.Contains("Required input(s) 'consult_draft' missing", resolution.Error);
    }

    [Fact]
    public void V7_UnknownInput_IsRejectedListingTheDeclaration()
    {
        var resolution = ConsultGenerationJobStarter.ResolveEffectiveInputs(
            new ConsultGenerationRequest(null, Inputs: new Dictionary<string, string>
            {
                ["consult_draft"] = "Draft.",
                ["labs"] = "CBC normal."
            }),
            V7Fixtures.MultiDeliverable());

        Assert.Contains("Unknown input(s) 'labs'", resolution.Error);
        Assert.Contains("declared: consult_draft, prior_notes", resolution.Error);
    }
}

/// <summary>Loads the real bundled catalog once for starter tests.</summary>
file static class TestCatalog
{
    public static readonly Consultologist.Api.Agents.OutputContractCatalog Instance = Load();

    private static Consultologist.Api.Agents.OutputContractCatalog Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Consultologist.sln")))
        {
            dir = dir.Parent;
        }

        return Consultologist.Api.Agents.OutputContractCatalog.Load(
            Path.Combine(dir!.FullName, "external", "consultologist-agents", "agents"));
    }
}
