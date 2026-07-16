using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Consultologist.Api.Workflow;

/// <summary>
/// GET Public/Chain — the anonymous, open-CORS view of the whole artifact
/// chain: repo-owned workflow packages, the output-contract catalog, and the
/// redacted agent definitions, each with versions and latest pointers. Backed
/// exclusively by PublicRegistryReader (no credential, no private client), so
/// acct-* content is unreachable by construction. Consumers: the app's
/// logged-out surface and the future marketing site (#95).
/// </summary>
public sealed class PublicChain
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

    private readonly PublicRegistryReader _reader;
    private readonly ILogger<PublicChain> _logger;

    private (PublicChainResponse Response, DateTimeOffset FetchedAt)? _cache;

    public PublicChain(PublicRegistryReader reader, ILogger<PublicChain> logger)
    {
        _reader = reader;
        _logger = logger;
    }

    [Function("PublicChain")]
    public async Task<HttpResponseData> GetChainAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "options", Route = "Public/Chain")] HttpRequestData req)
    {
        var cancellationToken = req.FunctionContext.CancellationToken;

        if (string.Equals(req.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            var optionsResponse = req.CreateResponse(HttpStatusCode.NoContent);
            FunctionCors.ApplyPublic(optionsResponse);
            return optionsResponse;
        }

        if (!_reader.IsConfigured)
        {
            var unconfigured = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            FunctionCors.ApplyPublic(unconfigured);
            await unconfigured.WriteAsJsonAsync(new { error = "The public registry is not configured." }, cancellationToken);
            return unconfigured;
        }

        PublicChainResponse chain;
        try
        {
            if (_cache is { } cached && DateTimeOffset.UtcNow - cached.FetchedAt < CacheDuration)
            {
                chain = cached.Response;
            }
            else
            {
                chain = await _reader.BuildChainAsync(cancellationToken);
                _cache = (chain, DateTimeOffset.UtcNow);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Public chain view failed against the public registry.");
            var errorResponse = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            FunctionCors.ApplyPublic(errorResponse);
            await errorResponse.WriteAsJsonAsync(new { error = "The public registry is unavailable." }, cancellationToken);
            return errorResponse;
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        FunctionCors.ApplyPublic(response);
        response.Headers.Add("Cache-Control", "public, max-age=60");
        await response.WriteAsJsonAsync(chain, cancellationToken);
        return response;
    }
}
