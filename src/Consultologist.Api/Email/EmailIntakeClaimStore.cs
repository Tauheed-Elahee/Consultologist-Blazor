using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Core;
using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;

namespace Consultologist.Api.Email;

public sealed record EmailIntakeClaim(
    string ClaimKey,
    string GraphMessageId,
    string? FromAddress,
    DateTimeOffset ClaimedAtUtc,
    string? AppUserId = null,
    string? JobId = null,
    string? Outcome = null);

public static class EmailIntakeOutcomes
{
    public const string Accepted = "accepted";
    public const string RejectedAuth = "rejected-auth";
    public const string RejectedSender = "rejected-sender";
    public const string RejectedEmpty = "rejected-empty";
    public const string StartFailed = "start-failed";
    public const string Vanished = "vanished";
}

public interface IEmailIntakeClaimStore
{
    /// <summary>Atomic claim: false when the message was already claimed (409).</summary>
    Task<bool> TryClaimAsync(EmailIntakeClaim claim, CancellationToken cancellationToken);

    Task<EmailIntakeClaim?> GetAsync(string claimKey, CancellationToken cancellationToken);

    Task UpdateAsync(EmailIntakeClaim claim, CancellationToken cancellationToken);
}

/// <summary>
/// The exactly-once ledger for email intake (#158): one row per inbound
/// message (keyed by internetMessageId), claimed atomically BEFORE the job
/// starts so a message can never start two jobs. Rows record disposition
/// metadata only — never subject or body.
/// </summary>
public sealed class TableEmailIntakeClaimStore : IEmailIntakeClaimStore
{
    private const string TableName = "EmailIntakeProcessed";
    internal const string PartitionKey = "email-intake";

    private readonly TableClient _table;
    private bool _tableEnsured;
    private readonly SemaphoreSlim _ensureLock = new(1, 1);

    public TableEmailIntakeClaimStore(IConfiguration configuration, TokenCredential credential)
    {
        _table = StorageTables.CreateClient(configuration, credential, TableName, "EmailIntakeStorage", "AccountStorage");
    }

    public async Task<bool> TryClaimAsync(EmailIntakeClaim claim, CancellationToken cancellationToken)
    {
        await EnsureTableAsync(cancellationToken);

        try
        {
            await _table.AddEntityAsync(ToEntity(claim), cancellationToken);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            return false;
        }
    }

    public async Task<EmailIntakeClaim?> GetAsync(string claimKey, CancellationToken cancellationToken)
    {
        await EnsureTableAsync(cancellationToken);

        try
        {
            var response = await _table.GetEntityAsync<EmailIntakeClaimEntity>(
                PartitionKey,
                CreateRowKey(claimKey),
                cancellationToken: cancellationToken);
            return ToClaim(response.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task UpdateAsync(EmailIntakeClaim claim, CancellationToken cancellationToken)
    {
        await EnsureTableAsync(cancellationToken);
        await _table.UpsertEntityAsync(ToEntity(claim), TableUpdateMode.Merge, cancellationToken);
    }

    private async Task EnsureTableAsync(CancellationToken cancellationToken)
    {
        if (_tableEnsured)
        {
            return;
        }

        await _ensureLock.WaitAsync(cancellationToken);
        try
        {
            if (!_tableEnsured)
            {
                await _table.CreateIfNotExistsAsync(cancellationToken);
                _tableEnsured = true;
            }
        }
        finally
        {
            _ensureLock.Release();
        }
    }

    // internetMessageId values contain <> and can contain characters illegal in
    // row keys — hash to base64url like AccountStore.CreateSubjectHash.
    internal static string CreateRowKey(string claimKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(claimKey));
        return Convert.ToBase64String(hash)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static EmailIntakeClaimEntity ToEntity(EmailIntakeClaim claim)
    {
        return new EmailIntakeClaimEntity
        {
            PartitionKey = PartitionKey,
            RowKey = CreateRowKey(claim.ClaimKey),
            ClaimKey = claim.ClaimKey,
            GraphMessageId = claim.GraphMessageId,
            FromAddress = claim.FromAddress,
            ClaimedAtUtc = claim.ClaimedAtUtc,
            AppUserId = claim.AppUserId,
            JobId = claim.JobId,
            Outcome = claim.Outcome
        };
    }

    private static EmailIntakeClaim ToClaim(EmailIntakeClaimEntity entity)
    {
        return new EmailIntakeClaim(
            entity.ClaimKey,
            entity.GraphMessageId,
            entity.FromAddress,
            entity.ClaimedAtUtc,
            entity.AppUserId,
            entity.JobId,
            entity.Outcome);
    }
}

public sealed class EmailIntakeClaimEntity : ITableEntity
{
    public string PartitionKey { get; set; } = TableEmailIntakeClaimStore.PartitionKey;
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
    public string ClaimKey { get; set; } = string.Empty;
    public string GraphMessageId { get; set; } = string.Empty;
    public string? FromAddress { get; set; }
    public DateTimeOffset ClaimedAtUtc { get; set; }
    public string? AppUserId { get; set; }
    public string? JobId { get; set; }
    public string? Outcome { get; set; }
}
