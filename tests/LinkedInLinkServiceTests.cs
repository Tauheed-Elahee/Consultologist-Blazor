using Consultologist.Api.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Consultologist.Api.Tests;

public class LinkedInLinkServiceTests
{
    private readonly ILinkedInLinkStateStore _stateStore = Substitute.For<ILinkedInLinkStateStore>();
    private readonly ILinkedInTokenClient _tokenClient = Substitute.For<ILinkedInTokenClient>();
    private readonly ILinkedInIdTokenValidator _idTokenValidator = Substitute.For<ILinkedInIdTokenValidator>();
    private readonly ILinkedInVerificationClient _verificationClient = Substitute.For<ILinkedInVerificationClient>();
    private readonly IAccountStore _accountStore = Substitute.For<IAccountStore>();

    private LinkedInLinkService CreateService()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LinkedIn:ClientId"] = "client-1",
                ["LinkedIn:RedirectUri"] = "https://api.example.com/api/Account/LinkedIn/Callback"
            })
            .Build();

        return new LinkedInLinkService(
            _stateStore,
            _tokenClient,
            _idTokenValidator,
            _verificationClient,
            _accountStore,
            configuration,
            NullLogger<LinkedInLinkService>.Instance);
    }

    private static LinkedInLinkState CreateState() =>
        new("state-1", "user-1", "nonce-1", "https://app.example.com");

    [Fact]
    public async Task BuildAuthorizationUrl_CreatesStateAndEncodesQuery()
    {
        _stateStore.CreateAsync("user-1", "https://app.example.com", Arg.Any<CancellationToken>())
            .Returns(CreateState());

        var url = await CreateService().BuildAuthorizationUrlAsync("user-1", "https://app.example.com", CancellationToken.None);

        Assert.StartsWith("https://www.linkedin.com/oauth/v2/authorization?response_type=code", url);
        Assert.Contains("&client_id=client-1", url);
        Assert.Contains("&redirect_uri=https%3A%2F%2Fapi.example.com%2Fapi%2FAccount%2FLinkedIn%2FCallback", url);
        Assert.Contains("&state=state-1", url);
        Assert.Contains("&nonce=nonce-1", url);
        Assert.Contains("&scope=openid%20profile%20email", url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Callback_MissingState_IsInvalidWithoutStoreLookup(string? state)
    {
        var result = await CreateService().HandleCallbackAsync("code", state, null, CancellationToken.None);

        Assert.Equal(LinkedInCallbackOutcome.StateInvalid, result.Outcome);
        Assert.Null(result.ReturnOrigin);
        await _tokenClient.DidNotReceiveWithAnyArgs().ExchangeCodeAsync(default!, default);
    }

    [Fact]
    public async Task Callback_UnknownState_IsInvalid()
    {
        _stateStore.TakeAsync("state-1", Arg.Any<CancellationToken>())
            .Returns((LinkedInLinkState?)null);

        var result = await CreateService().HandleCallbackAsync("code", "state-1", null, CancellationToken.None);

        Assert.Equal(LinkedInCallbackOutcome.StateInvalid, result.Outcome);
        Assert.Null(result.ReturnOrigin);
        await _tokenClient.DidNotReceiveWithAnyArgs().ExchangeCodeAsync(default!, default);
    }

    [Fact]
    public async Task Callback_ProviderError_ConsumesStateAndDenies()
    {
        _stateStore.TakeAsync("state-1", Arg.Any<CancellationToken>()).Returns(CreateState());

        var result = await CreateService().HandleCallbackAsync(null, "state-1", "user_cancelled_login", CancellationToken.None);

        Assert.Equal(LinkedInCallbackOutcome.Denied, result.Outcome);
        Assert.Equal("https://app.example.com", result.ReturnOrigin);
        await _stateStore.Received(1).TakeAsync("state-1", Arg.Any<CancellationToken>());
        await _tokenClient.DidNotReceiveWithAnyArgs().ExchangeCodeAsync(default!, default);
    }

    [Fact]
    public async Task Callback_ExchangeFailure_MapsToExchangeFailed()
    {
        _stateStore.TakeAsync("state-1", Arg.Any<CancellationToken>()).Returns(CreateState());
        _tokenClient.ExchangeCodeAsync("code", Arg.Any<CancellationToken>())
            .Returns((LinkedInTokenResponse?)null);

        var result = await CreateService().HandleCallbackAsync("code", "state-1", null, CancellationToken.None);

        Assert.Equal(LinkedInCallbackOutcome.ExchangeFailed, result.Outcome);
        Assert.Equal("https://app.example.com", result.ReturnOrigin);
    }

    [Fact]
    public async Task Callback_InvalidToken_MapsToTokenInvalid()
    {
        _stateStore.TakeAsync("state-1", Arg.Any<CancellationToken>()).Returns(CreateState());
        _tokenClient.ExchangeCodeAsync("code", Arg.Any<CancellationToken>())
            .Returns(new LinkedInTokenResponse("id-token", "access-token"));
        _idTokenValidator.ValidateAsync("id-token", "nonce-1", Arg.Any<CancellationToken>())
            .Returns((LinkedInIdentityClaims?)null);

        var result = await CreateService().HandleCallbackAsync("code", "state-1", null, CancellationToken.None);

        Assert.Equal(LinkedInCallbackOutcome.TokenInvalid, result.Outcome);
        await _accountStore.DidNotReceiveWithAnyArgs()
            .LinkIdentityAsync(default!, default!, default!, default!, default, default, default, default, default);
    }

    [Fact]
    public async Task Callback_HappyPath_LinksLinkedInIdentityWithClaims()
    {
        _stateStore.TakeAsync("state-1", Arg.Any<CancellationToken>()).Returns(CreateState());
        _tokenClient.ExchangeCodeAsync("code", Arg.Any<CancellationToken>())
            .Returns(new LinkedInTokenResponse("id-token", "access-token"));
        _idTokenValidator.ValidateAsync("id-token", "nonce-1", Arg.Any<CancellationToken>())
            .Returns(new LinkedInIdentityClaims("li-sub", "Pat Doe", "pat@example.com", "https://media.example/p.jpg"));
        _verificationClient.GetVerifiedCategoriesAsync("access-token", Arg.Any<CancellationToken>())
            .Returns(new[] { "IDENTITY", "WORKPLACE" });
        _accountStore.LinkIdentityAsync(
                "user-1", "linkedin", "https://www.linkedin.com/oauth", "li-sub",
                "Pat Doe", "pat@example.com", "https://media.example/p.jpg", "IDENTITY,WORKPLACE", Arg.Any<CancellationToken>())
            .Returns(IdentityLinkOutcome.Linked);

        var result = await CreateService().HandleCallbackAsync("code", "state-1", null, CancellationToken.None);

        Assert.Equal(LinkedInCallbackOutcome.Linked, result.Outcome);
        Assert.Equal("https://app.example.com", result.ReturnOrigin);
        await _accountStore.Received(1).LinkIdentityAsync(
            "user-1", "linkedin", "https://www.linkedin.com/oauth", "li-sub",
            "Pat Doe", "pat@example.com", "https://media.example/p.jpg", "IDENTITY,WORKPLACE", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Callback_ConflictingIdentity_MapsToAlreadyLinked()
    {
        _stateStore.TakeAsync("state-1", Arg.Any<CancellationToken>()).Returns(CreateState());
        _tokenClient.ExchangeCodeAsync("code", Arg.Any<CancellationToken>())
            .Returns(new LinkedInTokenResponse("id-token", "access-token"));
        _idTokenValidator.ValidateAsync("id-token", "nonce-1", Arg.Any<CancellationToken>())
            .Returns(new LinkedInIdentityClaims("li-sub", null, null, null));
        _accountStore.LinkIdentityAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(IdentityLinkOutcome.ConflictOtherUser);

        var result = await CreateService().HandleCallbackAsync("code", "state-1", null, CancellationToken.None);

        Assert.Equal(LinkedInCallbackOutcome.AlreadyLinked, result.Outcome);
        Assert.Equal("https://app.example.com", result.ReturnOrigin);
    }

    [Fact]
    public async Task Callback_VerificationUnavailable_StillLinksWithoutCategories()
    {
        _stateStore.TakeAsync("state-1", Arg.Any<CancellationToken>()).Returns(CreateState());
        _tokenClient.ExchangeCodeAsync("code", Arg.Any<CancellationToken>())
            .Returns(new LinkedInTokenResponse("id-token", "access-token"));
        _idTokenValidator.ValidateAsync("id-token", "nonce-1", Arg.Any<CancellationToken>())
            .Returns(new LinkedInIdentityClaims("li-sub", null, null, null));
        _verificationClient.GetVerifiedCategoriesAsync("access-token", Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<string>?)null);
        _accountStore.LinkIdentityAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(IdentityLinkOutcome.Linked);

        var result = await CreateService().HandleCallbackAsync("code", "state-1", null, CancellationToken.None);

        Assert.Equal(LinkedInCallbackOutcome.Linked, result.Outcome);
        await _accountStore.Received(1).LinkIdentityAsync(
            "user-1", "linkedin", "https://www.linkedin.com/oauth", "li-sub",
            null, null, null, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Callback_NoAccessToken_SkipsVerificationAndLinks()
    {
        _stateStore.TakeAsync("state-1", Arg.Any<CancellationToken>()).Returns(CreateState());
        _tokenClient.ExchangeCodeAsync("code", Arg.Any<CancellationToken>())
            .Returns(new LinkedInTokenResponse("id-token", null));
        _idTokenValidator.ValidateAsync("id-token", "nonce-1", Arg.Any<CancellationToken>())
            .Returns(new LinkedInIdentityClaims("li-sub", null, null, null));
        _accountStore.LinkIdentityAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(IdentityLinkOutcome.Linked);

        var result = await CreateService().HandleCallbackAsync("code", "state-1", null, CancellationToken.None);

        Assert.Equal(LinkedInCallbackOutcome.Linked, result.Outcome);
        await _verificationClient.DidNotReceiveWithAnyArgs().GetVerifiedCategoriesAsync(default!, default);
    }
}
