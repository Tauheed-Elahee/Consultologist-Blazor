using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;

namespace Consultologist.Web.Services.Workflow;

public interface IWorkflowEndpointService
{
    Task<WorkflowPackageResponse> GetCurrentPackageAsync();
}

public record WorkflowPackageSectionResponse(string Id, string Name, string Content);

public record WorkflowPackageResponse(
    string Name,
    string Version,
    int SpecVersion,
    IReadOnlyList<WorkflowPackageSectionResponse>? Sections = null)
{
    public string Ref => $"{Name}@{Version}";
}

public sealed class WorkflowEndpointService : IWorkflowEndpointService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly IAccessTokenProvider _accessTokenProvider;
    private readonly NavigationManager _navigation;
    private readonly ILogger<WorkflowEndpointService> _logger;

    public WorkflowEndpointService(
        HttpClient httpClient,
        IConfiguration configuration,
        IAccessTokenProvider accessTokenProvider,
        NavigationManager navigation,
        ILogger<WorkflowEndpointService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _accessTokenProvider = accessTokenProvider;
        _navigation = navigation;
        _logger = logger;
    }

    public async Task<WorkflowPackageResponse> GetCurrentPackageAsync()
    {
        var packageUrl = _configuration["AzureFunction:WorkflowPackageCurrentUrl"];

        if (string.IsNullOrWhiteSpace(packageUrl))
        {
            throw new InvalidOperationException("AzureFunction:WorkflowPackageCurrentUrl is not configured.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, packageUrl);
        await AddAuthorizationAsync(request);

        using var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError(
                "Workflow package endpoint failed with status {StatusCode}: {Error}",
                response.StatusCode,
                errorContent);

            throw new HttpRequestException($"Workflow package endpoint failed: {response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<WorkflowPackageResponse>()
            ?? throw new InvalidOperationException("Failed to deserialize workflow package response.");
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
}
