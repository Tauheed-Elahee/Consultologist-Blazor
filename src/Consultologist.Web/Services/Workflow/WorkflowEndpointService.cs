using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;

namespace Consultologist.Web.Services.Workflow;

public interface IWorkflowEndpointService
{
    Task<WorkflowPackageResponse> GetCurrentPackageAsync();
    Task<WorkflowPackageContentResponse> GetCurrentPackageContentAsync();
    Task<WorkflowPublishOutcome> PublishPackageAsync(WorkflowPackagePublishRequest request);
    Task<PublicChainView?> GetPublicChainAsync();
}

/// <summary>
/// Minimal mirror of the anonymous Public/Chain document — currently just the
/// catalog's contract → agent mapping, used to decorate the History provenance
/// panel with agent names (the job record stores contract → version only; names
/// are catalog metadata, served by the public chain view).
/// </summary>
public record PublicChainView(PublicCatalogView? OutputContracts);

public record PublicCatalogView(Dictionary<string, PublicContractView>? Contracts);

public record PublicContractView(string? AgentName);

public record WorkflowPackageSectionResponse(string Id, string Name, string Content);

public record WorkflowPackageResponse(
    string Name,
    string Version,
    int SpecVersion,
    IReadOnlyList<WorkflowPackageSectionResponse>? Sections = null)
{
    public string Ref => $"{Name}@{Version}";
}

/// <summary>
/// The editor's load half. The manifest rides as an opaque JsonElement: the
/// texts-only editor never modifies it, and round-tripping it verbatim to the
/// publish endpoint avoids mirroring the server's typed manifest (and its
/// binding-value converter) in the client.
/// </summary>
public record WorkflowPackageContentResponse(
    string Name,
    string Version,
    int SpecVersion,
    JsonElement Manifest,
    Dictionary<string, string> Files)
{
    public string Ref => $"{Name}@{Version}";
}

public record WorkflowPackagePublishRequest(
    string Source,
    JsonElement Manifest,
    Dictionary<string, string> Files);

public record WorkflowPackagePublishResponse(
    string Name,
    string Version,
    string Ref,
    List<string>? Warnings = null);

/// <summary>Success carries the response; failure carries the endpoint's error list for inline display.</summary>
public record WorkflowPublishOutcome(
    WorkflowPackagePublishResponse? Response,
    IReadOnlyList<string> Errors)
{
    public bool Succeeded => Response != null;
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

    public async Task<WorkflowPackageContentResponse> GetCurrentPackageContentAsync()
    {
        var contentUrl = _configuration["AzureFunction:WorkflowPackageContentUrl"];

        if (string.IsNullOrWhiteSpace(contentUrl))
        {
            throw new InvalidOperationException("AzureFunction:WorkflowPackageContentUrl is not configured.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, contentUrl);
        await AddAuthorizationAsync(request);

        using var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError(
                "Workflow package content endpoint failed with status {StatusCode}: {Error}",
                response.StatusCode,
                errorContent);

            throw new HttpRequestException($"Workflow package content endpoint failed: {response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<WorkflowPackageContentResponse>()
            ?? throw new InvalidOperationException("Failed to deserialize workflow package content response.");
    }

    public async Task<WorkflowPublishOutcome> PublishPackageAsync(WorkflowPackagePublishRequest request)
    {
        var publishUrl = _configuration["AzureFunction:WorkflowPackagePublishUrl"];

        if (string.IsNullOrWhiteSpace(publishUrl))
        {
            throw new InvalidOperationException("AzureFunction:WorkflowPackagePublishUrl is not configured.");
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, publishUrl)
        {
            Content = JsonContent.Create(request)
        };
        await AddAuthorizationAsync(httpRequest);

        using var response = await _httpClient.SendAsync(httpRequest);

        if (response.IsSuccessStatusCode)
        {
            var published = await response.Content.ReadFromJsonAsync<WorkflowPackagePublishResponse>()
                ?? throw new InvalidOperationException("Failed to deserialize publish response.");
            return new WorkflowPublishOutcome(published, Array.Empty<string>());
        }

        // 400/403 carry { errors: [...] } for inline display; anything else is a
        // transport-level failure.
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden)
        {
            var payload = await response.Content.ReadFromJsonAsync<PublishErrorPayload>();
            return new WorkflowPublishOutcome(
                null,
                payload?.Errors is { Count: > 0 } ? payload.Errors : new List<string> { $"Publish failed: {response.StatusCode}" });
        }

        var errorContent = await response.Content.ReadAsStringAsync();
        _logger.LogError(
            "Workflow package publish endpoint failed with status {StatusCode}: {Error}",
            response.StatusCode,
            errorContent);

        throw new HttpRequestException($"Workflow package publish endpoint failed: {response.StatusCode}");
    }

    public async Task<PublicChainView?> GetPublicChainAsync()
    {
        var chainUrl = _configuration["AzureFunction:PublicChainUrl"];

        if (string.IsNullOrWhiteSpace(chainUrl))
        {
            return null;
        }

        try
        {
            // Anonymous endpoint — no bearer token; a failure only costs the
            // display decoration, never the page.
            return await _httpClient.GetFromJsonAsync<PublicChainView>(chainUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Public chain view unavailable; agent names will not be decorated.");
            return null;
        }
    }

    private sealed record PublishErrorPayload(List<string>? Errors);

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
