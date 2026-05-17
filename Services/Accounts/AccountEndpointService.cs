using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;

namespace BlazorWasm.Services.Accounts;

public sealed class AccountEndpointService : IAccountEndpointService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly IAccessTokenProvider _accessTokenProvider;
    private readonly NavigationManager _navigation;
    private readonly ILogger<AccountEndpointService> _logger;

    public AccountEndpointService(
        HttpClient httpClient,
        IConfiguration configuration,
        IAccessTokenProvider accessTokenProvider,
        NavigationManager navigation,
        ILogger<AccountEndpointService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _accessTokenProvider = accessTokenProvider;
        _navigation = navigation;
        _logger = logger;
    }

    public async Task<AccountMeResponse> GetCurrentAccountAsync()
    {
        var accountUrl = _configuration["AzureFunction:AccountMeUrl"];

        if (string.IsNullOrWhiteSpace(accountUrl))
        {
            throw new InvalidOperationException("AzureFunction:AccountMeUrl is not configured.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, accountUrl);
        await AddAuthorizationAsync(request);

        using var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError(
                "Account endpoint failed with status {StatusCode}: {Error}",
                response.StatusCode,
                errorContent);

            throw new HttpRequestException($"Account endpoint failed: {response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<AccountMeResponse>()
            ?? throw new InvalidOperationException("Failed to deserialize account response.");
    }

    public async Task<AccountSettingResponse?> GetSettingAsync(string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, GetSettingUrl(key));
        await AddAuthorizationAsync(request);

        using var response = await _httpClient.SendAsync(request);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError(
                "Account setting endpoint failed with status {StatusCode}: {Error}",
                response.StatusCode,
                errorContent);

            throw new HttpRequestException($"Account setting endpoint failed: {response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<AccountSettingResponse>()
            ?? throw new InvalidOperationException("Failed to deserialize account setting response.");
    }

    public async Task SaveSettingAsync(string key, string value, string contentType)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, GetSettingUrl(key))
        {
            Content = JsonContent.Create(new SaveAccountSettingRequest(value, contentType))
        };
        await AddAuthorizationAsync(request);

        using var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError(
                "Account setting save endpoint failed with status {StatusCode}: {Error}",
                response.StatusCode,
                errorContent);

            throw new HttpRequestException($"Account setting save endpoint failed: {response.StatusCode}");
        }
    }

    public async Task DeleteSettingAsync(string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, GetSettingUrl(key));
        await AddAuthorizationAsync(request);

        using var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError(
                "Account setting delete endpoint failed with status {StatusCode}: {Error}",
                response.StatusCode,
                errorContent);

            throw new HttpRequestException($"Account setting delete endpoint failed: {response.StatusCode}");
        }
    }

    private async Task AddAuthorizationAsync(HttpRequestMessage request)
    {
        var apiScope = _configuration["AzureFunction:ApiScope"];

        if (string.IsNullOrWhiteSpace(apiScope))
        {
            throw new InvalidOperationException("AzureFunction:ApiScope is not configured.");
        }

        var tokenResult = await _accessTokenProvider.RequestAccessToken(new AccessTokenRequestOptions
        {
            Scopes = new[] { apiScope }
        });

        if (!tokenResult.TryGetToken(out var token))
        {
            throw new AccessTokenNotAvailableException(_navigation, tokenResult, new[] { apiScope });
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
    }

    private string GetSettingUrl(string key)
    {
        var accountUrl = _configuration["AzureFunction:AccountMeUrl"];

        if (string.IsNullOrWhiteSpace(accountUrl))
        {
            throw new InvalidOperationException("AzureFunction:AccountMeUrl is not configured.");
        }

        var settingsBaseUrl = accountUrl.EndsWith("/Me", StringComparison.OrdinalIgnoreCase)
            ? accountUrl[..^"/Me".Length] + "/Settings/"
            : accountUrl.TrimEnd('/') + "/Settings/";

        return settingsBaseUrl + Uri.EscapeDataString(key);
    }
}
