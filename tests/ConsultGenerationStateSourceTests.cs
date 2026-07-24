using Consultologist.Api.Jobs;

namespace Consultologist.Api.Tests;

public class ConsultGenerationStateSourceTests
{
    private static ConsultGenerationJobState CreateState(string? source)
    {
        var state = ConsultGenerationJobState.Create(
            "job-1",
            "user-1",
            new List<IReadOnlyDictionary<string, string>>
            {
                new Dictionary<string, string> { ["id"] = "hpi", ["name"] = "HPI" }
            });
        state.Source = source;
        return state;
    }

    [Fact]
    public void ToIndexEntry_CarriesSource()
    {
        Assert.Equal("email", CreateState("email").ToIndexEntry().Source);
    }

    [Fact]
    public void ToResponse_CarriesSource()
    {
        Assert.Equal("email", CreateState("email").ToResponse().Source);
    }

    [Fact]
    public void NullSource_RoundTripsForLegacyRecords()
    {
        var state = CreateState(null);

        Assert.Null(state.ToIndexEntry().Source);
        Assert.Null(state.ToResponse().Source);
    }

    [Fact]
    public async Task Initialize_StampsSourceOnceAndNeverOverwrites()
    {
        var stateProperty = typeof(ConsultGenerationJobEntity)
            .GetProperty("State", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!;
        var indexStore = NSubstitute.Substitute.For<IConsultGenerationJobIndexStore>();
        var entity = new ConsultGenerationJobEntity(indexStore);

        var items = new List<IReadOnlyDictionary<string, string>>
        {
            new Dictionary<string, string> { ["id"] = "hpi", ["name"] = "HPI" }
        };

        await entity.Initialize(new ConsultGenerationJobInitialize("job-1", "user-1", items, Source: "email"));
        Assert.Equal("email", ((ConsultGenerationJobState)stateProperty.GetValue(entity)!).Source);

        // The orchestrator's defensive re-Initialize must not overwrite.
        await entity.Initialize(new ConsultGenerationJobInitialize("job-1", "user-1", items, Source: "app"));
        Assert.Equal("email", ((ConsultGenerationJobState)stateProperty.GetValue(entity)!).Source);
    }
}
