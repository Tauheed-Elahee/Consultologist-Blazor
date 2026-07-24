using Consultologist.Api.Jobs;
using Consultologist.Api.Models;
using Consultologist.Api.Workflow;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Client.Entities;
using Microsoft.DurableTask.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Consultologist.Api.Tests;

public class ConsultGenerationJobStarterTests
{
    private readonly IWorkflowPackageStore _packageStore = Substitute.For<IWorkflowPackageStore>();
    private readonly IWorkflowPackagePinResolver _pinResolver = Substitute.For<IWorkflowPackagePinResolver>();
    private readonly DurableTaskClient _client = Substitute.For<DurableTaskClient>("test");
    private readonly DurableEntityClient _entities = Substitute.For<DurableEntityClient>("test");

    private ConsultGenerationJobStarter CreateStarter()
    {
        _client.Entities.Returns(_entities);

        return new ConsultGenerationJobStarter(
            NullLogger<ConsultGenerationJobStarter>.Instance,
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
