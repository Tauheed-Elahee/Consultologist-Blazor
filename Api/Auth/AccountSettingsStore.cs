using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;

namespace Api.Auth;

public interface IAccountSettingsStore
{
    Task<AccountSetting?> GetAsync(string appUserId, string key, CancellationToken cancellationToken);
    Task SaveAsync(string appUserId, string key, string value, string contentType, CancellationToken cancellationToken);
    Task DeleteAsync(string appUserId, string key, CancellationToken cancellationToken);
}

public sealed class AccountSettingsStore : IAccountSettingsStore
{
    private const string AccountSettingsTableName = "AccountSettings";
    private readonly TableClient _settings;

    public AccountSettingsStore(IConfiguration configuration)
    {
        var connectionStringName = configuration["AccountStorage:ConnectionStringName"] ?? "AzureWebJobsStorage";
        var connectionString = configuration[connectionStringName]
            ?? Environment.GetEnvironmentVariable(connectionStringName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException($"{connectionStringName} is not configured for account settings storage.");
        }

        _settings = new TableClient(connectionString, AccountSettingsTableName);
    }

    public async Task<AccountSetting?> GetAsync(string appUserId, string key, CancellationToken cancellationToken)
    {
        await EnsureTableAsync(cancellationToken);

        try
        {
            var response = await _settings.GetEntityAsync<AccountSettingEntity>(
                appUserId,
                key,
                cancellationToken: cancellationToken);

            var entity = response.Value;
            return new AccountSetting(entity.RowKey, entity.Value, entity.ContentType, entity.UpdatedAtUtc);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task SaveAsync(
        string appUserId,
        string key,
        string value,
        string contentType,
        CancellationToken cancellationToken)
    {
        await EnsureTableAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var entity = new AccountSettingEntity
        {
            PartitionKey = appUserId,
            RowKey = key,
            Value = value,
            ContentType = contentType,
            UpdatedAtUtc = now
        };

        await _settings.UpsertEntityAsync(entity, TableUpdateMode.Replace, cancellationToken);
    }

    public async Task DeleteAsync(string appUserId, string key, CancellationToken cancellationToken)
    {
        await EnsureTableAsync(cancellationToken);

        try
        {
            await _settings.DeleteEntityAsync(appUserId, key, cancellationToken: cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
        }
    }

    private async Task EnsureTableAsync(CancellationToken cancellationToken)
    {
        await _settings.CreateIfNotExistsAsync(cancellationToken);
    }
}
