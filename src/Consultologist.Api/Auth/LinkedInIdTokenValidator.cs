using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Consultologist.Api.Auth;

public interface ILinkedInIdTokenValidator
{
    Task<LinkedInIdentityClaims?> ValidateAsync(string idToken, string expectedNonce, CancellationToken cancellationToken);
}

/// <summary>
/// Validates LinkedIn id_tokens for the identity-linking flow (#133). A
/// separate class from BearerTokenValidator because that singleton is bound
/// to the Entra authority; this one is fixed to LinkedIn's single issuer and
/// audience (the LinkedIn app's client id). LinkedIn identities are never
/// accepted as bearer credentials — this only proves account control once,
/// at link time.
/// </summary>
public sealed class LinkedInIdTokenValidator : ILinkedInIdTokenValidator
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<LinkedInIdTokenValidator> _logger;
    private readonly ConfigurationManager<OpenIdConnectConfiguration> _configurationManager;
    private readonly JwtSecurityTokenHandler _tokenHandler = new();

    public LinkedInIdTokenValidator(IConfiguration configuration, ILogger<LinkedInIdTokenValidator> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _tokenHandler.MapInboundClaims = false;
        _configurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            LinkedInOidc.DiscoveryUrl,
            new OpenIdConnectConfigurationRetriever());
    }

    public async Task<LinkedInIdentityClaims?> ValidateAsync(string idToken, string expectedNonce, CancellationToken cancellationToken)
    {
        var clientId = _configuration["LinkedIn:ClientId"];

        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException("LinkedIn:ClientId is not configured.");
        }

        try
        {
            var oidcConfiguration = await _configurationManager.GetConfigurationAsync(cancellationToken);

            var principal = _tokenHandler.ValidateToken(idToken, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = LinkedInOidc.Issuer,
                ValidateAudience = true,
                ValidAudience = clientId,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(2),
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = oidcConfiguration.SigningKeys,
                NameClaimType = "name"
            }, out _);

            return ReadClaims(principal, expectedNonce);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LinkedIn id_token validation failed.");
            return null;
        }
    }

    internal static LinkedInIdentityClaims? ReadClaims(ClaimsPrincipal principal, string expectedNonce)
    {
        // LinkedIn does not echo the nonce into its id_tokens (their discovery
        // document lists no nonce claim), so the check is opportunistic: absent
        // is accepted, present must match. The single-use server-bound state is
        // the flow's primary replay/CSRF defense.
        var nonce = principal.FindFirstValue("nonce");

        if (nonce != null && !string.Equals(nonce, expectedNonce, StringComparison.Ordinal))
        {
            return null;
        }

        var subject = principal.FindFirstValue("sub");

        if (string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        return new LinkedInIdentityClaims(
            subject,
            principal.FindFirstValue("name"),
            principal.FindFirstValue("email"),
            principal.FindFirstValue("picture"));
    }
}
