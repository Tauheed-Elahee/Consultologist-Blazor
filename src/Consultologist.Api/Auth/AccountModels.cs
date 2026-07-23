using Azure;
using Azure.Data.Tables;

namespace Consultologist.Api.Auth;

public static class AccountStatuses
{
    public const string Pending = "Pending";
    public const string Active = "Active";
    public const string Disabled = "Disabled";
}

public static class IdentityProviders
{
    // The credential authority: only entra-external-id identities can sign in.
    public const string EntraExternalId = "entra-external-id";
    // Verification signal only (#133): linked for proof of account control,
    // never accepted as a bearer credential.
    public const string LinkedIn = "linkedin";
}

public sealed class AppUserEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "app-user";
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string Status { get; set; } = AccountStatuses.Pending;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset LastSeenAtUtc { get; set; }
}

public sealed class IdentityLinkEntity : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty;
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
    public string AppUserId { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string SubjectHash { get; set; } = string.Empty;
    public DateTimeOffset LinkedAtUtc { get; set; }
    public DateTimeOffset LastSeenAtUtc { get; set; }
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    public string? PictureUrl { get; set; }
}

public sealed class UserIdentityLinkEntity : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty;
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string SubjectHash { get; set; } = string.Empty;
    public DateTimeOffset LinkedAtUtc { get; set; }
    public DateTimeOffset LastSeenAtUtc { get; set; }
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    public string? PictureUrl { get; set; }
}

public sealed record AuthenticatedUser(
    string Provider,
    string Issuer,
    string Subject,
    string DisplayName,
    string? Email,
    IReadOnlyList<string> Scopes);

public sealed record AppAccount(
    string AppUserId,
    string DisplayName,
    string? Email,
    string Status,
    AccountIdentity CurrentIdentity,
    IReadOnlyList<AccountIdentity> LinkedIdentities);

public sealed record AccountIdentity(
    string Provider,
    string Issuer,
    string Subject,
    DateTimeOffset LinkedAt,
    DateTimeOffset LastSeenAt,
    string? DisplayName = null,
    string? Email = null,
    string? PictureUrl = null);

public sealed record AccountMeResponse(
    string AppUserId,
    string DisplayName,
    string? Email,
    string Status,
    AccountIdentity CurrentIdentity,
    IReadOnlyList<AccountIdentity> LinkedIdentities);

public sealed class AccountSettingEntity : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty;
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
    public string Value { get; set; } = string.Empty;
    public string ContentType { get; set; } = "text/plain";
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed record AccountSetting(
    string Key,
    string Value,
    string ContentType,
    DateTimeOffset UpdatedAtUtc);

public sealed record AccountSettingResponse(
    string Key,
    string Value,
    string ContentType,
    DateTimeOffset UpdatedAtUtc);

public sealed record SaveAccountSettingRequest(
    string? Value,
    string? ContentType);
