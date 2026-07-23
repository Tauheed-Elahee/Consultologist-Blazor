namespace Consultologist.Web.Services.Accounts;

public sealed record AccountMeResponse(
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
    string? PictureUrl = null,
    string? VerifiedCategories = null);

public sealed record LinkedInStartResponse(string AuthorizationUrl);

public sealed record AccountSettingResponse(
    string Key,
    string Value,
    string ContentType,
    DateTimeOffset UpdatedAtUtc);

public sealed record SaveAccountSettingRequest(
    string Value,
    string ContentType);

public sealed record AccountJobSummaryResponse(
    string JobId,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    int TotalBlockCount,
    int CompletedBlockCount,
    int FailedBlockCount);

public sealed record AccountJobsResponse(
    IReadOnlyList<AccountJobSummaryResponse> Jobs,
    string? ContinuationToken);
