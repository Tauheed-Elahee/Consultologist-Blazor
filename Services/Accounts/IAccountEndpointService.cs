namespace BlazorWasm.Services.Accounts;

public interface IAccountEndpointService
{
    Task<AccountMeResponse> GetCurrentAccountAsync();
    Task<AccountSettingResponse?> GetSettingAsync(string key);
    Task SaveSettingAsync(string key, string value, string contentType);
    Task DeleteSettingAsync(string key);
}
