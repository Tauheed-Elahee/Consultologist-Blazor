using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Consultologist.Api.Auth;

public interface ILinkedInVerificationClient
{
    /// <summary>
    /// Fetches the member's Verified on LinkedIn categories (e.g. IDENTITY,
    /// WORKPLACE) with their access token. Best-effort: any failure returns
    /// null and must never block linking — on the Development tier LinkedIn
    /// returns 403 for members who are not admins of the developer app.
    /// </summary>
    Task<IReadOnlyList<string>?> GetVerifiedCategoriesAsync(string accessToken, CancellationToken cancellationToken);
}

public sealed class LinkedInVerificationClient : ILinkedInVerificationClient
{
    private const string DefaultApiVersion = "202510";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LinkedInVerificationClient> _logger;

    public LinkedInVerificationClient(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<LinkedInVerificationClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>?> GetVerifiedCategoriesAsync(string accessToken, CancellationToken cancellationToken)
    {
        var apiVersion = _configuration["LinkedIn:ApiVersion"];

        using var request = new HttpRequestMessage(HttpMethod.Get, LinkedInOidc.VerificationReportEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("LinkedIn-Version", string.IsNullOrWhiteSpace(apiVersion) ? DefaultApiVersion : apiVersion);

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "LinkedIn verification report unavailable. StatusCode={StatusCode}",
                    (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            return ReadCategories(document.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "LinkedIn verification report request failed.");
            return null;
        }
    }

    // An empty verifications array and an unavailable report both read as
    // null: on the Development tier a 403 is the norm for non-admin members,
    // so "checked but unverified" cannot be distinguished reliably anyway.
    internal static IReadOnlyList<string>? ReadCategories(JsonElement root)
    {
        if (!root.TryGetProperty("verifications", out var verifications)
            || verifications.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var categories = verifications.EnumerateArray()
            .Where(element => element.ValueKind == JsonValueKind.String)
            .Select(element => element.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();

        return categories.Length > 0 ? categories : null;
    }
}
