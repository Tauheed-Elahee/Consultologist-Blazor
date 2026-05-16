using Azure;
using Azure.Data.Tables;

namespace Api.Auth;

public static class AccountStatuses
{
    public const string Active = "Active";
    public const string Disabled = "Disabled";
}

public sealed class AppUserEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "app-user";
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string Status { get; set; } = AccountStatuses.Active;
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
    DateTimeOffset LastSeenAt);

public sealed record AccountMeResponse(
    string AppUserId,
    string DisplayName,
    string? Email,
    string Status,
    AccountIdentity CurrentIdentity,
    IReadOnlyList<AccountIdentity> LinkedIdentities);
