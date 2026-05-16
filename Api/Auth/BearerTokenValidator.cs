using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Api.Auth;

public interface IBearerTokenValidator
{
    Task<AuthenticatedUser?> ValidateAsync(string? authorizationHeader, CancellationToken cancellationToken);
}

public sealed class BearerTokenValidator : IBearerTokenValidator
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<BearerTokenValidator> _logger;
    private readonly ConfigurationManager<OpenIdConnectConfiguration> _configurationManager;
    private readonly JwtSecurityTokenHandler _tokenHandler = new();

    public BearerTokenValidator(IConfiguration configuration, ILogger<BearerTokenValidator> logger)
    {
        _configuration = configuration;
        _logger = logger;

        var authority = GetRequiredConfiguration("Auth:Authority").TrimEnd('/');
        _configurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            $"{authority}/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever());
    }

    public async Task<AuthenticatedUser?> ValidateAsync(string? authorizationHeader, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader)
            || !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = authorizationHeader["Bearer ".Length..].Trim();

        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        try
        {
            var authority = GetRequiredConfiguration("Auth:Authority").TrimEnd('/');
            var audience = GetRequiredConfiguration("Auth:Audience");
            var requiredScope = _configuration["Auth:RequiredScope"];
            var oidcConfiguration = await _configurationManager.GetConfigurationAsync(cancellationToken);

            var principal = _tokenHandler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuers = GetValidIssuers(authority, oidcConfiguration.Issuer),
                ValidateAudience = true,
                ValidAudience = audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(2),
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = oidcConfiguration.SigningKeys,
                NameClaimType = "name"
            }, out _);

            var scopes = GetScopes(principal).ToArray();

            if (!string.IsNullOrWhiteSpace(requiredScope)
                && !scopes.Contains(requiredScope, StringComparer.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Bearer token missing required scope. RequiredScope={RequiredScope}", requiredScope);
                return null;
            }

            var issuer = principal.FindFirstValue("iss") ?? authority;
            var subject = principal.FindFirstValue("oid")
                ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? principal.FindFirstValue("sub");

            if (string.IsNullOrWhiteSpace(subject))
            {
                _logger.LogWarning("Bearer token did not contain a stable subject claim.");
                return null;
            }

            var displayName = principal.FindFirstValue("name")
                ?? principal.FindFirstValue("preferred_username")
                ?? principal.FindFirstValue("email")
                ?? "Clinician";

            var email = principal.FindFirstValue("email")
                ?? principal.FindFirstValue("preferred_username")
                ?? principal.FindFirstValue("upn");

            return new AuthenticatedUser(
                "entra-external-id",
                issuer,
                subject,
                displayName,
                email,
                scopes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bearer token validation failed.");
            return null;
        }
    }

    private string GetRequiredConfiguration(string key)
    {
        return string.IsNullOrWhiteSpace(_configuration[key])
            ? throw new InvalidOperationException($"{key} is not configured.")
            : _configuration[key]!;
    }

    private static IEnumerable<string> GetValidIssuers(string authority, string metadataIssuer)
    {
        yield return authority;
        yield return metadataIssuer;

        if (authority.Contains("/v2.0", StringComparison.OrdinalIgnoreCase))
        {
            yield return authority.Replace("/v2.0", string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            yield return $"{authority}/v2.0";
        }
    }

    private static IEnumerable<string> GetScopes(ClaimsPrincipal principal)
    {
        foreach (var claimType in new[] { "scp", "scope" })
        {
            var claim = principal.FindFirstValue(claimType);

            if (string.IsNullOrWhiteSpace(claim))
            {
                continue;
            }

            foreach (var scope in claim.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return scope;
            }
        }
    }
}
