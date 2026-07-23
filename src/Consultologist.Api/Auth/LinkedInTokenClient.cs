using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Consultologist.Api.Auth;

public interface ILinkedInTokenClient
{
    Task<LinkedInTokenResponse?> ExchangeCodeAsync(string code, CancellationToken cancellationToken);
}

public sealed class LinkedInTokenClient : ILinkedInTokenClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LinkedInTokenClient> _logger;

    public LinkedInTokenClient(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<LinkedInTokenClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<LinkedInTokenResponse?> ExchangeCodeAsync(string code, CancellationToken cancellationToken)
    {
        var clientId = GetRequired("LinkedIn:ClientId");
        var clientSecret = GetRequired("LinkedIn:ClientSecret");
        var redirectUri = GetRequired("LinkedIn:RedirectUri");

        using var request = new HttpRequestMessage(HttpMethod.Post, LinkedInOidc.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["redirect_uri"] = redirectUri
            })
        };

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Status only — the body can echo request parameters.
                _logger.LogWarning(
                    "LinkedIn token exchange failed. StatusCode={StatusCode}",
                    (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            var idToken = document.RootElement.TryGetProperty("id_token", out var idTokenElement)
                ? idTokenElement.GetString()
                : null;
            var accessToken = document.RootElement.TryGetProperty("access_token", out var accessTokenElement)
                ? accessTokenElement.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(idToken))
            {
                _logger.LogWarning("LinkedIn token exchange succeeded but returned no id_token.");
                return null;
            }

            return new LinkedInTokenResponse(idToken, accessToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "LinkedIn token exchange request failed.");
            return null;
        }
    }

    private string GetRequired(string key)
    {
        return string.IsNullOrWhiteSpace(_configuration[key])
            ? throw new InvalidOperationException($"{key} is not configured.")
            : _configuration[key]!;
    }
}
