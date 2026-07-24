using System.Reflection;
using Consultologist.Api.Jobs;
using Consultologist.Api.Models;
using NSubstitute;

namespace Consultologist.Api.Tests;

public class ScheduledRunTests
{
    [Fact]
    public void ValidateRequest_NoSchedule_IsValid()
    {
        Assert.Null(ConsultGenerationJobs.ValidateRequest(new ConsultGenerationRequest("draft")));
    }

    [Fact]
    public void ValidateRequest_WithinHorizon_IsValid()
    {
        var request = new ConsultGenerationRequest("draft", ScheduledAtUtc: DateTimeOffset.UtcNow.AddHours(8));

        Assert.Null(ConsultGenerationJobs.ValidateRequest(request));
    }

    [Fact]
    public void ValidateRequest_PastSchedule_IsValid_RunsImmediately()
    {
        var request = new ConsultGenerationRequest("draft", ScheduledAtUtc: DateTimeOffset.UtcNow.AddMinutes(-10));

        Assert.Null(ConsultGenerationJobs.ValidateRequest(request));
    }

    [Fact]
    public void ValidateRequest_BeyondHorizon_IsRejected()
    {
        var request = new ConsultGenerationRequest("draft", ScheduledAtUtc: DateTimeOffset.UtcNow.AddDays(8));

        Assert.Equal("ScheduledAtUtc is more than 7 days out.", ConsultGenerationJobs.ValidateRequest(request));
    }

    private static readonly PropertyInfo StateProperty = typeof(ConsultGenerationJobEntity)
        .GetProperty("State", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;

    private static ConsultGenerationJobState StateOf(ConsultGenerationJobEntity entity) =>
        (ConsultGenerationJobState)StateProperty.GetValue(entity)!;

    private static readonly List<IReadOnlyDictionary<string, string>> Items = new()
    {
        new Dictionary<string, string> { ["id"] = "hpi", ["name"] = "HPI" }
    };

    private static ConsultGenerationJobEntity CreateEntity() =>
        new(Substitute.For<IConsultGenerationJobIndexStore>());

    [Fact]
    public async Task Initialize_FutureSchedule_LandsScheduled()
    {
        var entity = CreateEntity();
        var scheduledAt = DateTimeOffset.UtcNow.AddHours(6);

        await entity.Initialize(new ConsultGenerationJobInitialize("job-1", "user-1", Items, ScheduledAtUtc: scheduledAt));

        var state = StateOf(entity);
        Assert.Equal(ConsultGenerationJobStatuses.Scheduled, state.Status);
        Assert.Equal(scheduledAt, state.ScheduledAtUtc);
    }

    [Fact]
    public async Task Initialize_PastSchedule_StaysQueued()
    {
        var entity = CreateEntity();

        await entity.Initialize(new ConsultGenerationJobInitialize(
            "job-1", "user-1", Items, ScheduledAtUtc: DateTimeOffset.UtcNow.AddMinutes(-5)));

        Assert.Equal(ConsultGenerationJobStatuses.Queued, StateOf(entity).Status);
    }

    [Fact]
    public async Task Initialize_NoSchedule_StaysQueued()
    {
        var entity = CreateEntity();

        await entity.Initialize(new ConsultGenerationJobInitialize("job-1", "user-1", Items));

        var state = StateOf(entity);
        Assert.Equal(ConsultGenerationJobStatuses.Queued, state.Status);
        Assert.Null(state.ScheduledAtUtc);
    }

    [Fact]
    public async Task MarkRunning_FlipsScheduledToRunning()
    {
        var entity = CreateEntity();
        await entity.Initialize(new ConsultGenerationJobInitialize(
            "job-1", "user-1", Items, ScheduledAtUtc: DateTimeOffset.UtcNow.AddHours(6)));

        await entity.MarkRunning();

        Assert.Equal(ConsultGenerationJobStatuses.Running, StateOf(entity).Status);
    }

    [Fact]
    public void IndexEntryAndResponse_CarryScheduledAtUtc()
    {
        var state = ConsultGenerationJobState.Create("job-1", "user-1", Items);
        var scheduledAt = DateTimeOffset.UtcNow.AddHours(6);
        state.ScheduledAtUtc = scheduledAt;

        Assert.Equal(scheduledAt, state.ToIndexEntry().ScheduledAtUtc);
        Assert.Equal(scheduledAt, state.ToResponse().ScheduledAtUtc);
    }
}
