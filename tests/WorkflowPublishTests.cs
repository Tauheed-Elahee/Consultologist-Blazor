using Consultologist.Api.Agents;
using Consultologist.Api.Auth;
using Consultologist.Api.Workflow;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Consultologist.Api.Tests;

/// <summary>
/// The publish slice of the in-app editor (#57): CalVer assignment, acct-*
/// naming and the owner-only access rule, and the publisher pipeline against
/// in-memory fakes — stamping, structural guards, validator wiring, the
/// conditional-create retry, activation, and the content→publish round trip.
/// </summary>
public class CalVerAssignNextTests
{
    private static DateTimeOffset At(int year, int month) => new(year, month, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FirstPublish_OpensTheCurrentMonth()
    {
        Assert.Equal("v2026.07.1", CalVerVersion.AssignNext(null, At(2026, 7)).ToString());
    }

    [Fact]
    public void SameMonth_IncrementsTheCounter()
    {
        CalVerVersion.TryParse("v2026.07.3", out var latest);
        Assert.Equal("v2026.07.4", CalVerVersion.AssignNext(latest, At(2026, 7)).ToString());
    }

    [Fact]
    public void NewMonth_RestartsTheCounter()
    {
        CalVerVersion.TryParse("v2026.06.9", out var latest);
        Assert.Equal("v2026.07.1", CalVerVersion.AssignNext(latest, At(2026, 7)).ToString());
    }

    [Fact]
    public void YearRollover_RestartsTheCounter()
    {
        CalVerVersion.TryParse("v2025.12.5", out var latest);
        Assert.Equal("v2026.01.1", CalVerVersion.AssignNext(latest, At(2026, 1)).ToString());
    }

    [Fact]
    public void LatestAheadOfClock_KeepsItsMonth_NeverMovesBackwards()
    {
        CalVerVersion.TryParse("v2026.08.2", out var latest);
        Assert.Equal("v2026.08.3", CalVerVersion.AssignNext(latest, At(2026, 7)).ToString());
    }
}

public class WorkflowPackageNamingTests
{
    private const string OwnerId = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void ForAccount_TakesTwelveHex_Lowercased()
    {
        Assert.Equal("acct-0123456789ab", WorkflowPackageNaming.ForAccount(OwnerId));
        Assert.Equal("acct-0123456789ab", WorkflowPackageNaming.ForAccount(OwnerId.ToUpperInvariant()));
    }

    [Fact]
    public void ForAccount_RejectsShortIds()
    {
        Assert.Throws<ArgumentException>(() => WorkflowPackageNaming.ForAccount("abc"));
    }

    [Theory]
    [InlineData("general", true)]
    [InlineData("breast-oncology", true)]
    [InlineData("acct-0123456789ab", true)]
    [InlineData("acct-999999999999", false)]
    [InlineData("acct-0123456789abcd", false)]
    public void CanAccess_Matrix(string name, bool expected)
    {
        Assert.Equal(expected, WorkflowPackageNaming.CanAccess(name, OwnerId));
    }

    [Theory]
    [InlineData("acct-0123456789ab", true)]
    [InlineData("general", false)]
    [InlineData("account-x", false)]
    public void IsAccountPackage_ByPrefix(string name, bool expected)
    {
        Assert.Equal(expected, WorkflowPackageNaming.IsAccountPackage(name));
    }
}

public class ForeignPinFallbackTests
{
    private const string OwnerId = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task ForeignAccountPin_FallsThroughToDefault()
    {
        var settings = new FakeSettingsStore();
        await settings.SaveAsync(OwnerId, WorkflowPackagePinResolver.PackagePinSettingKey, "acct-999999999999@v2026.07.1", "text/plain", CancellationToken.None);
        var resolver = new WorkflowPackagePinResolver(settings, NullLogger<WorkflowPackagePinResolver>.Instance);

        var resolved = await resolver.ResolvePinAsync(OwnerId, CancellationToken.None);

        Assert.Equal("general", resolved.Name);
    }

    [Fact]
    public async Task OwnAccountPin_Resolves()
    {
        var settings = new FakeSettingsStore();
        await settings.SaveAsync(OwnerId, WorkflowPackagePinResolver.PackagePinSettingKey, "acct-0123456789ab@v2026.07.1", "text/plain", CancellationToken.None);
        var resolver = new WorkflowPackagePinResolver(settings, NullLogger<WorkflowPackagePinResolver>.Instance);

        var resolved = await resolver.ResolvePinAsync(OwnerId, CancellationToken.None);

        Assert.Equal("acct-0123456789ab@v2026.07.1", resolved.ToString());
    }
}

public class WorkflowPackagePublisherTests
{
    private const string OwnerId = "0123456789abcdef0123456789abcdef";
    private const string AccountName = "acct-0123456789ab";
    private const string SourceRef = "general@v2026.08.1";

    private static readonly OutputContractCatalog Catalog = LoadCatalog();

    private static OutputContractCatalog LoadCatalog()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "agents", "output-contracts.json")))
        {
            dir = dir.Parent;
        }

        return OutputContractCatalog.Load(Path.Combine(dir!.FullName, "agents"));
    }

    private static (WorkflowPackagePublisher Publisher, FakeRegistryWriter Writer, FakeSettingsStore Settings) CreatePublisher(
        DateTimeOffset? nowUtc = null,
        FakeRegistryWriter? writer = null)
    {
        writer ??= new FakeRegistryWriter();
        var settings = new FakeSettingsStore();
        var store = new FakePackageStore(SourceRef);
        var publisher = new WorkflowPackagePublisher(
            store,
            writer,
            settings,
            Catalog,
            new FixedTimeProvider(nowUtc ?? new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero)),
            NullLogger<WorkflowPackagePublisher>.Instance);
        return (publisher, writer, settings);
    }

    private static WorkflowPackagePublishRequest Request(
        string? source = SourceRef,
        WorkflowPackageManifest? manifest = null,
        Dictionary<string, string>? files = null)
    {
        manifest ??= V5Fixtures.Manifest();
        files ??= V5Fixtures.Files(manifest);
        return new WorkflowPackagePublishRequest(source, manifest, files);
    }

    [Fact]
    public async Task Publish_StampsNameVersionAndLineage_UploadsAndActivates()
    {
        var (publisher, writer, settings) = CreatePublisher();
        // The client's name/version/derivedFrom are hostile on purpose.
        var manifest = V5Fixtures.Manifest() with { Name = "general", Version = "v9999.01.1", DerivedFrom = "evil@v2020.01.1" };

        var result = await publisher.PublishAsync(OwnerId, Request(manifest: manifest, files: V5Fixtures.Files(manifest)), CancellationToken.None);

        Assert.True(result.Succeeded, string.Join(" | ", result.Errors));
        Assert.Equal(AccountName, result.Response!.Name);
        Assert.Equal("v2026.07.1", result.Response.Version);
        Assert.Equal($"{AccountName}@v2026.07.1", result.Response.Ref);

        var stored = writer.ReadManifest(AccountName, "v2026.07.1");
        Assert.Equal(AccountName, stored.Name);
        Assert.Equal("v2026.07.1", stored.Version);
        Assert.Equal(SourceRef, stored.DerivedFrom);

        Assert.Equal("v2026.07.1", writer.LatestPointers[AccountName]);
        var pin = await settings.GetAsync(OwnerId, WorkflowPackagePinResolver.PackagePinSettingKey, CancellationToken.None);
        Assert.Equal($"{AccountName}@v2026.07.1", pin!.Value);
    }

    [Fact]
    public async Task Publish_RoundTrips_EveryFileByteIdentical_AndManifestRevalidates()
    {
        var (publisher, writer, _) = CreatePublisher();
        var manifest = V5Fixtures.Manifest();
        var files = V5Fixtures.Files(manifest);

        var result = await publisher.PublishAsync(OwnerId, Request(manifest: manifest, files: files), CancellationToken.None);

        Assert.True(result.Succeeded, string.Join(" | ", result.Errors));

        foreach (var (path, content) in files)
        {
            Assert.Equal(content, writer.Blobs[$"{AccountName}/v2026.07.1/{path}"]);
        }

        // The stored manifest deserializes and passes the same validator again —
        // the registry copy is as valid as the request was.
        var reparsed = writer.ReadManifest(AccountName, "v2026.07.1");
        var revalidated = V5Fixtures.Validate(reparsed, files);
        Assert.True(revalidated.IsValid, string.Join(" | ", revalidated.Errors));
    }

    [Fact]
    public async Task Publish_SecondVersion_IncrementsFromLatest()
    {
        var (publisher, writer, _) = CreatePublisher();
        Assert.True((await publisher.PublishAsync(OwnerId, Request(), CancellationToken.None)).Succeeded);

        var second = await publisher.PublishAsync(OwnerId, Request(), CancellationToken.None);

        Assert.True(second.Succeeded);
        Assert.Equal("v2026.07.2", second.Response!.Version);
        Assert.Equal("v2026.07.2", writer.LatestPointers[AccountName]);
    }

    [Fact]
    public async Task Publish_VersionConflict_RereadsBumpsAndRetries()
    {
        // A manifest already exists at the candidate version but the latest
        // pointer lags behind (stale pointer) — the retry must bump past the
        // collision, not re-collide.
        var writer = new FakeRegistryWriter();
        await writer.CreateManifestAsync(AccountName, "v2026.07.1", "{}", CancellationToken.None);
        var (publisher, _, _) = CreatePublisher(writer: writer);

        var result = await publisher.PublishAsync(OwnerId, Request(), CancellationToken.None);

        Assert.True(result.Succeeded, string.Join(" | ", result.Errors));
        Assert.Equal("v2026.07.2", result.Response!.Version);
        Assert.Equal("v2026.07.2", writer.LatestPointers[AccountName]);
    }

    [Fact]
    public async Task Publish_ForeignSource_IsForbidden()
    {
        var (publisher, _, _) = CreatePublisher();

        var result = await publisher.PublishAsync(OwnerId, Request(source: "acct-999999999999@v2026.07.1"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(result.Forbidden);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not a ref")]
    [InlineData("general@latest")]
    public async Task Publish_RejectsMissingOrNonConcreteSource(string? source)
    {
        var (publisher, _, _) = CreatePublisher();

        var result = await publisher.PublishAsync(OwnerId, Request(source: source), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(result.Forbidden);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task Publish_UnresolvableSource_Errors()
    {
        var (publisher, _, _) = CreatePublisher();

        var result = await publisher.PublishAsync(OwnerId, Request(source: "general@v2020.01.1"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Contains("could not be resolved", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("wwwroot/evil.md")]
    [InlineData("prompts/..")]
    [InlineData("data/standards/nested/deeper.md")]
    [InlineData("prompts/sub/dir.md")]
    public async Task Publish_RejectsIllegalPaths(string path)
    {
        var (publisher, _, _) = CreatePublisher();
        var manifest = V5Fixtures.Manifest();
        var files = V5Fixtures.Files(manifest);
        files[path] = "content";

        var result = await publisher.PublishAsync(OwnerId, Request(manifest: manifest, files: files), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Contains(path, StringComparison.Ordinal) && error.Contains("not allowed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Publish_RejectsStrayFiles_TheReverseClosure()
    {
        var (publisher, _, _) = CreatePublisher();
        var manifest = V5Fixtures.Manifest();
        var files = V5Fixtures.Files(manifest);
        files["prompts/unreferenced.md"] = "not in the manifest";

        var result = await publisher.PublishAsync(OwnerId, Request(manifest: manifest, files: files), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Contains("prompts/unreferenced.md", StringComparison.Ordinal) && error.Contains("not referenced", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Publish_RejectsOversizedFiles()
    {
        var (publisher, _, _) = CreatePublisher();
        var manifest = V5Fixtures.Manifest();
        var files = V5Fixtures.Files(manifest);
        files[V5Fixtures.StandardsDir + "hpi.md"] = new string('x', 300 * 1024);

        var result = await publisher.PublishAsync(OwnerId, Request(manifest: manifest, files: files), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Contains("per-file limit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Publish_InvalidPackage_ReturnsValidatorErrors_AndWritesNothing()
    {
        var (publisher, writer, settings) = CreatePublisher();
        var manifest = V5Fixtures.Manifest();
        var files = V5Fixtures.Files(manifest);
        files.Remove("prompts/identify-problem.md");

        var result = await publisher.PublishAsync(OwnerId, Request(manifest: manifest, files: files), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.Errors);
        Assert.Empty(writer.Blobs);
        Assert.Null(await settings.GetAsync(OwnerId, WorkflowPackagePinResolver.PackagePinSettingKey, CancellationToken.None));
    }
}

internal sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _nowUtc;

    public FixedTimeProvider(DateTimeOffset nowUtc)
    {
        _nowUtc = nowUtc;
    }

    public override DateTimeOffset GetUtcNow() => _nowUtc;
}

internal sealed class FakeRegistryWriter : IWorkflowPackageRegistryWriter
{
    public Dictionary<string, string> Blobs { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> LatestPointers { get; } = new(StringComparer.Ordinal);
    private readonly HashSet<string> _manifests = new(StringComparer.Ordinal);

    public Task<string?> ReadLatestVersionAsync(string name, CancellationToken cancellationToken) =>
        Task.FromResult(LatestPointers.TryGetValue(name, out var version) ? version : null);

    public Task UploadFileAsync(string name, string version, string path, string content, CancellationToken cancellationToken)
    {
        Blobs[$"{name}/{version}/{path}"] = content;
        return Task.CompletedTask;
    }

    public Task CreateManifestAsync(string name, string version, string manifestJson, CancellationToken cancellationToken)
    {
        var key = $"{name}/{version}/manifest.json";

        if (!_manifests.Add(key))
        {
            throw new WorkflowPackageVersionConflictException(
                $"{name}@{version}",
                new InvalidOperationException("manifest exists"));
        }

        Blobs[key] = manifestJson;
        return Task.CompletedTask;
    }

    public Task SetLatestPointerAsync(string name, string version, CancellationToken cancellationToken)
    {
        LatestPointers[name] = version;
        return Task.CompletedTask;
    }

    public WorkflowPackageManifest ReadManifest(string name, string version)
    {
        var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return System.Text.Json.JsonSerializer.Deserialize<WorkflowPackageManifest>(
            Blobs[$"{name}/{version}/manifest.json"], options)!;
    }
}

internal sealed class FakePackageStore : IWorkflowPackageStore
{
    private readonly string _knownRef;

    public FakePackageStore(string knownRef)
    {
        _knownRef = knownRef;
    }

    public Task<WorkflowPackage> ResolveAsync(WorkflowPackageRef packageRef, CancellationToken cancellationToken)
    {
        if (!string.Equals(packageRef.ToString(), _knownRef, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Workflow package blob '{packageRef}' was not found (fake).");
        }

        var manifest = V5Fixtures.Manifest();
        return Task.FromResult(new WorkflowPackage(manifest, SourceFiles: V5Fixtures.Files(manifest)));
    }
}

internal sealed class FakeSettingsStore : IAccountSettingsStore
{
    private readonly Dictionary<(string AppUserId, string Key), AccountSetting> _settings = new();

    public Task<AccountSetting?> GetAsync(string appUserId, string key, CancellationToken cancellationToken) =>
        Task.FromResult(_settings.TryGetValue((appUserId, key), out var setting) ? setting : null);

    public Task SaveAsync(string appUserId, string key, string value, string contentType, CancellationToken cancellationToken)
    {
        _settings[(appUserId, key)] = new AccountSetting(key, value, contentType, DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string appUserId, string key, CancellationToken cancellationToken)
    {
        _settings.Remove((appUserId, key));
        return Task.CompletedTask;
    }
}

public class ContainerRoutingTests
{
    private static WorkflowPackageBlobContainerFactory CreateFactory(bool withPublicUri)
    {
        var settings = new Dictionary<string, string?>
        {
            ["WorkflowPackages:ConnectionStringName"] = "TestRegistryConnection",
            ["TestRegistryConnection"] = "UseDevelopmentStorage=true"
        };

        if (withPublicUri)
        {
            settings["WorkflowPackages:PublicBlobServiceUri"] = "https://public.example.net";
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        return new WorkflowPackageBlobContainerFactory(
            configuration,
            NSubstitute.Substitute.For<Azure.Core.TokenCredential>(),
            NullLogger<WorkflowPackageBlobContainerFactory>.Instance);
    }

    [Fact]
    public void RepoOwnedName_RoutesToThePublicContainer()
    {
        var factory = CreateFactory(withPublicUri: true);
        Assert.Equal("public.example.net", factory.GetContainerFor("general").Uri.Host);
    }

    [Fact]
    public void AccountName_RoutesToThePrivateContainer_EvenWithPublicConfigured()
    {
        var factory = CreateFactory(withPublicUri: true);
        Assert.NotEqual("public.example.net", factory.GetContainerFor("acct-0123456789ab").Uri.Host);
    }

    [Fact]
    public void NoPublicUri_EverythingRoutesPrivate()
    {
        var factory = CreateFactory(withPublicUri: false);
        Assert.Equal(factory.GetContainer().Uri, factory.GetContainerFor("general").Uri);
    }

    [Fact]
    public void TheWriterTarget_IsAlwaysThePrivateContainer()
    {
        var factory = CreateFactory(withPublicUri: true);
        Assert.NotEqual("public.example.net", factory.GetContainer().Uri.Host);
    }
}
