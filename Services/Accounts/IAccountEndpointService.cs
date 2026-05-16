namespace BlazorWasm.Services.Accounts;

public interface IAccountEndpointService
{
    Task<AccountMeResponse> GetCurrentAccountAsync();
}
