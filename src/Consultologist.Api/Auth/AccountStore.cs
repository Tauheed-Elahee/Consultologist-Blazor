using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Core;
using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Consultologist.Api.Auth;

public interface IAccountStore
{
    Task<AppAccount> ResolveOrCreateAsync(AuthenticatedUser user, CancellationToken cancellationToken);
}

public sealed class AccountStore : IAccountStore
{
    private const string AppUsersTableName = "AppUsers";
    private const string IdentityLinksTableName = "IdentityLinks";
    private const string UserIdentityLinksTableName = "UserIdentityLinks";
    private readonly TableClient _appUsers;
    private readonly TableClient _identityLinks;
    private readonly TableClient _userIdentityLinks;
    private readonly ILogger<AccountStore> _logger;

    public AccountStore(IConfiguration configuration, TokenCredential credential, ILogger<AccountStore> logger)
    {
        _logger = logger;
        _appUsers = StorageTables.CreateClient(configuration, credential, AppUsersTableName, "AccountStorage");
        _identityLinks = StorageTables.CreateClient(configuration, credential, IdentityLinksTableName, "AccountStorage");
        _userIdentityLinks = StorageTables.CreateClient(configuration, credential, UserIdentityLinksTableName, "AccountStorage");
    }

    public async Task<AppAccount> ResolveOrCreateAsync(AuthenticatedUser user, CancellationToken cancellationToken)
    {
        await EnsureTablesAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var subjectHash = CreateSubjectHash(user.Provider, user.Issuer, user.Subject);
        var linkPartitionKey = user.Provider;
        var linkRowKey = subjectHash;
        IdentityLinkEntity? identityLink = null;

        try
        {
            var identityLinkResponse = await _identityLinks.GetEntityAsync<IdentityLinkEntity>(
                linkPartitionKey,
                linkRowKey,
                cancellationToken: cancellationToken);
            identityLink = identityLinkResponse.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            var appUserId = Guid.NewGuid().ToString("N");
            var newAppUser = new AppUserEntity
            {
                RowKey = appUserId,
                DisplayName = user.DisplayName,
                Email = user.Email,
                Status = AccountStatuses.Active,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                LastSeenAtUtc = now
            };

            identityLink = new IdentityLinkEntity
            {
                PartitionKey = linkPartitionKey,
                RowKey = linkRowKey,
                AppUserId = appUserId,
                Provider = user.Provider,
                Issuer = user.Issuer,
                Subject = user.Subject,
                SubjectHash = subjectHash,
                LinkedAtUtc = now,
                LastSeenAtUtc = now
            };

            await _appUsers.UpsertEntityAsync(newAppUser, TableUpdateMode.Replace, cancellationToken);
            await _identityLinks.UpsertEntityAsync(identityLink, TableUpdateMode.Replace, cancellationToken);
            await _userIdentityLinks.UpsertEntityAsync(ToUserIdentityLink(identityLink), TableUpdateMode.Replace, cancellationToken);

            _logger.LogInformation(
                "Created app user from identity. AppUserId={AppUserId}, Provider={Provider}, Issuer={Issuer}",
                appUserId,
                user.Provider,
                user.Issuer);
        }

        identityLink.LastSeenAtUtc = now;
        await _identityLinks.UpsertEntityAsync(identityLink, TableUpdateMode.Replace, cancellationToken);
        await _userIdentityLinks.UpsertEntityAsync(ToUserIdentityLink(identityLink), TableUpdateMode.Replace, cancellationToken);

        var appUserEntity = await _appUsers.GetEntityAsync<AppUserEntity>(
            "app-user",
            identityLink.AppUserId,
            cancellationToken: cancellationToken);

        var appUser = appUserEntity.Value;
        appUser.DisplayName = string.IsNullOrWhiteSpace(appUser.DisplayName) ? user.DisplayName : appUser.DisplayName;
        appUser.Email = appUser.Email ?? user.Email;
        appUser.UpdatedAtUtc = now;
        appUser.LastSeenAtUtc = now;

        await _appUsers.UpsertEntityAsync(appUser, TableUpdateMode.Replace, cancellationToken);

        var linkedIdentities = await GetLinkedIdentitiesAsync(appUser.RowKey, cancellationToken);
        var currentIdentity = new AccountIdentity(
            identityLink.Provider,
            identityLink.Issuer,
            identityLink.Subject,
            identityLink.LinkedAtUtc,
            identityLink.LastSeenAtUtc);

        return new AppAccount(
            appUser.RowKey,
            appUser.DisplayName,
            appUser.Email,
            appUser.Status,
            currentIdentity,
            linkedIdentities);
    }

    private async Task EnsureTablesAsync(CancellationToken cancellationToken)
    {
        await _appUsers.CreateIfNotExistsAsync(cancellationToken);
        await _identityLinks.CreateIfNotExistsAsync(cancellationToken);
        await _userIdentityLinks.CreateIfNotExistsAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<AccountIdentity>> GetLinkedIdentitiesAsync(
        string appUserId,
        CancellationToken cancellationToken)
    {
        var identities = new List<AccountIdentity>();

        await foreach (var entity in _userIdentityLinks.QueryAsync<UserIdentityLinkEntity>(
                           link => link.PartitionKey == appUserId,
                           cancellationToken: cancellationToken))
        {
            identities.Add(new AccountIdentity(
                entity.Provider,
                entity.Issuer,
                entity.SubjectHash,
                entity.LinkedAtUtc,
                entity.LastSeenAtUtc));
        }

        return identities;
    }

    private static UserIdentityLinkEntity ToUserIdentityLink(IdentityLinkEntity identityLink)
    {
        return new UserIdentityLinkEntity
        {
            PartitionKey = identityLink.AppUserId,
            RowKey = $"{identityLink.Provider}-{identityLink.SubjectHash}",
            Provider = identityLink.Provider,
            Issuer = identityLink.Issuer,
            SubjectHash = identityLink.SubjectHash,
            LinkedAtUtc = identityLink.LinkedAtUtc,
            LastSeenAtUtc = identityLink.LastSeenAtUtc
        };
    }

    private static string CreateSubjectHash(string provider, string issuer, string subject)
    {
        var input = $"{provider}|{issuer}|{subject}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToBase64String(hash)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
