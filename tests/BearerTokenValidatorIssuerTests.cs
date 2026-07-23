using Consultologist.Api.Auth;
using Microsoft.IdentityModel.Tokens;

namespace Consultologist.Api.Tests;

public class BearerTokenValidatorIssuerTests
{
    private const string CommonAuthority = "https://login.microsoftonline.com/common/v2.0";
    private const string OrganizationsAuthority = "https://login.microsoftonline.com/organizations/v2.0";
    private const string TemplateIssuer = "https://login.microsoftonline.com/{tenantid}/v2.0";
    private const string MsaConsumerIssuer = "https://login.microsoftonline.com/9188040d-6c67-4c5b-b112-36a304b66dad/v2.0";
    private const string OrgTenantIssuer = "https://login.microsoftonline.com/3f2504e0-4f89-41d3-9a0c-0305e82c3301/v2.0";

    [Theory]
    [InlineData(CommonAuthority, MsaConsumerIssuer)]
    [InlineData(CommonAuthority, OrgTenantIssuer)]
    [InlineData(OrganizationsAuthority, OrgTenantIssuer)]
    public void ValidateIssuer_AcceptsTenantIssuersUnderMultiTenantAuthorities(string authority, string issuer)
    {
        Assert.Equal(issuer, BearerTokenValidator.ValidateIssuer(authority, TemplateIssuer, issuer));
    }

    [Theory]
    [InlineData("https://evil.example.com/9188040d-6c67-4c5b-b112-36a304b66dad/v2.0")]
    [InlineData("https://login.microsoftonline.com/9188040d-6c67-4c5b-b112-36a304b66dad")]
    public void ValidateIssuer_RejectsForeignHostsAndWrongTokenVersions(string issuer)
    {
        Assert.Throws<SecurityTokenInvalidIssuerException>(
            () => BearerTokenValidator.ValidateIssuer(CommonAuthority, TemplateIssuer, issuer));
    }

    [Fact]
    public void ValidateIssuer_AcceptsTenantedAuthorityExactIssuerOnly()
    {
        var authority = "https://login.microsoftonline.com/3f2504e0-4f89-41d3-9a0c-0305e82c3301/v2.0";

        Assert.Equal(OrgTenantIssuer, BearerTokenValidator.ValidateIssuer(authority, OrgTenantIssuer, OrgTenantIssuer));
        Assert.Throws<SecurityTokenInvalidIssuerException>(
            () => BearerTokenValidator.ValidateIssuer(authority, OrgTenantIssuer, MsaConsumerIssuer));
    }

    [Theory]
    [InlineData(CommonAuthority, MsaConsumerIssuer, true)]
    [InlineData(OrganizationsAuthority, OrgTenantIssuer, true)]
    [InlineData(CommonAuthority, "https://sts.windows.net/9188040d-6c67-4c5b-b112-36a304b66dad/v2.0", false)]
    public void MatchesAuthorityTenantIssuer_MatchesHostAndTokenVersion(string authority, string issuer, bool expected)
    {
        Assert.Equal(expected, BearerTokenValidator.MatchesAuthorityTenantIssuer(authority, issuer));
    }

    [Theory]
    [InlineData(TemplateIssuer, MsaConsumerIssuer, true)]
    [InlineData(TemplateIssuer, "https://evil.example.com/tenant/v2.0", false)]
    [InlineData(OrgTenantIssuer, OrgTenantIssuer, false)]
    public void MatchesTenantIssuerTemplate_RequiresTemplateBounds(string metadataIssuer, string issuer, bool expected)
    {
        Assert.Equal(expected, BearerTokenValidator.MatchesTenantIssuerTemplate(metadataIssuer, issuer));
    }
}
