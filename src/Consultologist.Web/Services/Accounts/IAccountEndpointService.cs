namespace Consultologist.Web.Services.Accounts;

public interface IAccountEndpointService
{
    Task<AccountMeResponse> GetCurrentAccountAsync();
    Task<string> StartLinkedInLinkAsync();
    Task<AccountSettingResponse?> GetSettingAsync(string key);
    Task SaveSettingAsync(string key, string value, string contentType);
    Task DeleteSettingAsync(string key);
    Task<AccountJobsResponse> GetJobsAsync(int limit = 20, string? continuationToken = null);
}
