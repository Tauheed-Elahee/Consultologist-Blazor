using Azure;
using Azure.Data.Tables;

namespace Consultologist.Api.Auth;

/// <summary>
/// LinkedIn's OIDC surface (Sign In with LinkedIn using OpenID Connect).
/// Fixed values — LinkedIn publishes one issuer for all tenants.
/// </summary>
public static class LinkedInOidc
{
    public const string Issuer = "https://www.linkedin.com/oauth";
    public const string DiscoveryUrl = "https://www.linkedin.com/oauth/.well-known/openid-configuration";
    public const string AuthorizationEndpoint = "https://www.linkedin.com/oauth/v2/authorization";
    public const string TokenEndpoint = "https://www.linkedin.com/oauth/v2/accessToken";
    public const string VerificationReportEndpoint = "https://api.linkedin.com/rest/verificationReport";
    // r_verify comes from the Verified on LinkedIn product attached to the
    // LinkedIn app (Development/Lite tier scope).
    public const string Scopes = "openid profile email r_verify";
}

public sealed class LinkedInLinkStateEntity : ITableEntity
{
    public string PartitionKey { get; set; } = LinkedInLinkStateStore.StatePartitionKey;
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
    public string AppUserId { get; set; } = string.Empty;
    public string Nonce { get; set; } = string.Empty;
    public string ReturnOrigin { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
}

public sealed record LinkedInLinkState(
    string State,
    string AppUserId,
    string Nonce,
    string ReturnOrigin);

// The access token is used for exactly one read-only call — the Verified on
// LinkedIn verificationReport — then discarded; the flow never writes to
// LinkedIn on the user's behalf.
public sealed record LinkedInTokenResponse(string? IdToken, string? AccessToken);

public sealed record LinkedInIdentityClaims(
    string Subject,
    string? Name,
    string? Email,
    string? Picture);

public enum LinkedInCallbackOutcome
{
    Linked,
    StateInvalid,
    Denied,
    ExchangeFailed,
    TokenInvalid,
    AlreadyLinked
}

public sealed record LinkedInCallbackResult(
    LinkedInCallbackOutcome Outcome,
    string? ReturnOrigin);
