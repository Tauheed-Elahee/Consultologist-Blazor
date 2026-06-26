using System.Globalization;
using System.Text;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;

namespace Api;

public interface IConsultGenerationJobIndexStore
{
    Task UpsertAsync(ConsultGenerationJobIndexEntry entry, CancellationToken cancellationToken);

    Task<(IReadOnlyList<ConsultGenerationJobIndexEntry> Jobs, string? ContinuationToken)> ListAsync(
        string appUserId,
        int limit,
        string? continuationToken,
        CancellationToken cancellationToken);
}

internal sealed class TableConsultGenerationJobIndexStore : IConsultGenerationJobIndexStore
{
    private const string IndexTableName = "ConsultGenerationJobIndex";

    private readonly TableClient _index;
    private readonly SemaphoreSlim _ensureTableLock = new(1, 1);
    private bool _tableEnsured;

    public TableConsultGenerationJobIndexStore(IConfiguration configuration)
    {
        var connectionStringName = configuration["ConsultGenerationJobIndexStorage:ConnectionStringName"]
            ?? configuration["AccountStorage:ConnectionStringName"]
            ?? "AzureWebJobsStorage";
        var connectionString = configuration[connectionStringName]
            ?? Environment.GetEnvironmentVariable(connectionStringName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"{connectionStringName} is not configured for consult generation job index storage.");
        }

        _index = new TableClient(connectionString, IndexTableName);
    }

    public async Task UpsertAsync(ConsultGenerationJobIndexEntry entry, CancellationToken cancellationToken)
    {
        await EnsureTableAsync(cancellationToken);
        await _index.UpsertEntityAsync(ToEntity(entry), TableUpdateMode.Replace, cancellationToken);
    }

    public async Task<(IReadOnlyList<ConsultGenerationJobIndexEntry> Jobs, string? ContinuationToken)> ListAsync(
        string appUserId,
        int limit,
        string? continuationToken,
        CancellationToken cancellationToken)
    {
        await EnsureTableAsync(cancellationToken);

        var decodedToken = DecodeToken(continuationToken);
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {appUserId}");
        var jobs = new List<ConsultGenerationJobIndexEntry>(limit);
        string? nextToken = null;

        await foreach (var page in _index
            .QueryAsync<ConsultGenerationJobIndexEntity>(filter, maxPerPage: limit, cancellationToken: cancellationToken)
            .AsPages(decodedToken))
        {
            foreach (var entity in page.Values)
            {
                jobs.Add(ToEntry(entity));
            }

            nextToken = EncodeToken(page.ContinuationToken);
            break;
        }

        return (jobs, nextToken);
    }

    private async Task EnsureTableAsync(CancellationToken cancellationToken)
    {
        if (_tableEnsured)
        {
            return;
        }

        await _ensureTableLock.WaitAsync(cancellationToken);

        try
        {
            if (_tableEnsured)
            {
                return;
            }

            await _index.CreateIfNotExistsAsync(cancellationToken);
            _tableEnsured = true;
        }
        finally
        {
            _ensureTableLock.Release();
        }
    }

    private static ConsultGenerationJobIndexEntity ToEntity(ConsultGenerationJobIndexEntry entry)
    {
        return new ConsultGenerationJobIndexEntity
        {
            PartitionKey = entry.AppUserId,
            RowKey = FormatRowKey(entry.CreatedAtUtc, entry.JobId),
            JobId = entry.JobId,
            Status = entry.Status,
            CreatedAtUtc = entry.CreatedAtUtc,
            StartedAtUtc = entry.StartedAtUtc,
            CompletedAtUtc = entry.CompletedAtUtc,
            TotalSectionCount = entry.TotalSectionCount,
            CompletedSectionCount = entry.CompletedSectionCount,
            FailedSectionCount = entry.FailedSectionCount
        };
    }

    private static ConsultGenerationJobIndexEntry ToEntry(ConsultGenerationJobIndexEntity entity)
    {
        return new ConsultGenerationJobIndexEntry(
            entity.JobId,
            entity.PartitionKey,
            entity.Status,
            entity.CreatedAtUtc,
            entity.StartedAtUtc,
            entity.CompletedAtUtc,
            entity.TotalSectionCount,
            entity.CompletedSectionCount,
            entity.FailedSectionCount);
    }

    private static string FormatRowKey(DateTimeOffset createdAtUtc, string jobId)
    {
        var reverseTimestamp = (DateTimeOffset.MaxValue.Ticks - createdAtUtc.UtcTicks)
            .ToString("D19", CultureInfo.InvariantCulture);
        return $"{reverseTimestamp}_{jobId}";
    }

    private static string? EncodeToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(token));
    }

    private static string? DecodeToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(token));
        }
        catch
        {
            return null;
        }
    }
}

public sealed record ConsultGenerationJobIndexEntry(
    string JobId,
    string AppUserId,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    int TotalSectionCount,
    int CompletedSectionCount,
    int FailedSectionCount);

internal sealed class ConsultGenerationJobIndexEntity : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty;
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
    public string JobId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public int TotalSectionCount { get; set; }
    public int CompletedSectionCount { get; set; }
    public int FailedSectionCount { get; set; }
}
