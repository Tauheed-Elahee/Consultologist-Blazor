using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Consultologist.Api.Auth;

public interface ILinkedInLinkService
{
    Task<string> BuildAuthorizationUrlAsync(string appUserId, string returnOrigin, CancellationToken cancellationToken);

    Task<LinkedInCallbackResult> HandleCallbackAsync(string? code, string? state, string? error, CancellationToken cancellationToken);
}

public sealed class LinkedInLinkService : ILinkedInLinkService
{
    private readonly ILinkedInLinkStateStore _stateStore;
    private readonly ILinkedInTokenClient _tokenClient;
    private readonly ILinkedInIdTokenValidator _idTokenValidator;
    private readonly IAccountStore _accountStore;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LinkedInLinkService> _logger;

    public LinkedInLinkService(
        ILinkedInLinkStateStore stateStore,
        ILinkedInTokenClient tokenClient,
        ILinkedInIdTokenValidator idTokenValidator,
        IAccountStore accountStore,
        IConfiguration configuration,
        ILogger<LinkedInLinkService> logger)
    {
        _stateStore = stateStore;
        _tokenClient = tokenClient;
        _idTokenValidator = idTokenValidator;
        _accountStore = accountStore;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<string> BuildAuthorizationUrlAsync(string appUserId, string returnOrigin, CancellationToken cancellationToken)
    {
        var clientId = GetRequired("LinkedIn:ClientId");
        var redirectUri = GetRequired("LinkedIn:RedirectUri");
        var state = await _stateStore.CreateAsync(appUserId, returnOrigin, cancellationToken);

        return BuildAuthorizationUrl(clientId, redirectUri, state.State, state.Nonce);
    }

    public async Task<LinkedInCallbackResult> HandleCallbackAsync(string? code, string? state, string? error, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            return new LinkedInCallbackResult(LinkedInCallbackOutcome.StateInvalid, null);
        }

        // Consume the state before anything else — even an error callback must
        // burn it so the URL cannot be replayed.
        var linkState = await _stateStore.TakeAsync(state, cancellationToken);

        if (linkState == null)
        {
            return new LinkedInCallbackResult(LinkedInCallbackOutcome.StateInvalid, null);
        }

        if (!string.IsNullOrWhiteSpace(error) || string.IsNullOrWhiteSpace(code))
        {
            _logger.LogInformation(
                "LinkedIn link denied or aborted. AppUserId={AppUserId}, Error={Error}",
                linkState.AppUserId,
                error ?? "no code");
            return new LinkedInCallbackResult(LinkedInCallbackOutcome.Denied, linkState.ReturnOrigin);
        }

        var tokenResponse = await _tokenClient.ExchangeCodeAsync(code, cancellationToken);

        if (tokenResponse?.IdToken == null)
        {
            return new LinkedInCallbackResult(LinkedInCallbackOutcome.ExchangeFailed, linkState.ReturnOrigin);
        }

        var claims = await _idTokenValidator.ValidateAsync(tokenResponse.IdToken, linkState.Nonce, cancellationToken);

        if (claims == null)
        {
            return new LinkedInCallbackResult(LinkedInCallbackOutcome.TokenInvalid, linkState.ReturnOrigin);
        }

        var outcome = await _accountStore.LinkIdentityAsync(
            linkState.AppUserId,
            IdentityProviders.LinkedIn,
            LinkedInOidc.Issuer,
            claims.Subject,
            claims.Name,
            claims.Email,
            claims.Picture,
            cancellationToken);

        return outcome == IdentityLinkOutcome.ConflictOtherUser
            ? new LinkedInCallbackResult(LinkedInCallbackOutcome.AlreadyLinked, linkState.ReturnOrigin)
            : new LinkedInCallbackResult(LinkedInCallbackOutcome.Linked, linkState.ReturnOrigin);
    }

    internal static string BuildAuthorizationUrl(string clientId, string redirectUri, string state, string nonce)
    {
        return LinkedInOidc.AuthorizationEndpoint
            + "?response_type=code"
            + $"&client_id={Uri.EscapeDataString(clientId)}"
            + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
            + $"&state={Uri.EscapeDataString(state)}"
            + $"&nonce={Uri.EscapeDataString(nonce)}"
            + $"&scope={Uri.EscapeDataString(LinkedInOidc.Scopes)}";
    }

    private string GetRequired(string key)
    {
        return string.IsNullOrWhiteSpace(_configuration[key])
            ? throw new InvalidOperationException($"{key} is not configured.")
            : _configuration[key]!;
    }
}
