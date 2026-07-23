using System.Security.Claims;
using Consultologist.Api.Auth;

namespace Consultologist.Api.Tests;

public class LinkedInIdTokenValidatorTests
{
    private static ClaimsPrincipal CreatePrincipal(params (string Type, string Value)[] claims)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
            claims.Select(c => new Claim(c.Type, c.Value))));
    }

    [Fact]
    public void ReadClaims_ExtractsSubjectAndProfileClaims()
    {
        var principal = CreatePrincipal(
            ("nonce", "nonce-1"),
            ("sub", "li-sub"),
            ("name", "Pat Doe"),
            ("email", "pat@example.com"),
            ("picture", "https://media.example/p.jpg"));

        var claims = LinkedInIdTokenValidator.ReadClaims(principal, "nonce-1");

        Assert.NotNull(claims);
        Assert.Equal("li-sub", claims.Subject);
        Assert.Equal("Pat Doe", claims.Name);
        Assert.Equal("pat@example.com", claims.Email);
        Assert.Equal("https://media.example/p.jpg", claims.Picture);
    }

    [Fact]
    public void ReadClaims_MissingProfileClaims_StillReturnsSubject()
    {
        var principal = CreatePrincipal(("nonce", "nonce-1"), ("sub", "li-sub"));

        var claims = LinkedInIdTokenValidator.ReadClaims(principal, "nonce-1");

        Assert.NotNull(claims);
        Assert.Null(claims.Name);
        Assert.Null(claims.Email);
        Assert.Null(claims.Picture);
    }

    [Theory]
    [InlineData("wrong-nonce")]
    [InlineData("")]
    public void ReadClaims_NonceMismatch_ReturnsNull(string tokenNonce)
    {
        var principal = CreatePrincipal(("nonce", tokenNonce), ("sub", "li-sub"));

        Assert.Null(LinkedInIdTokenValidator.ReadClaims(principal, "nonce-1"));
    }

    [Fact]
    public void ReadClaims_MissingNonceClaim_ReturnsNull()
    {
        var principal = CreatePrincipal(("sub", "li-sub"));

        Assert.Null(LinkedInIdTokenValidator.ReadClaims(principal, "nonce-1"));
    }

    [Fact]
    public void ReadClaims_MissingSubject_ReturnsNull()
    {
        var principal = CreatePrincipal(("nonce", "nonce-1"));

        Assert.Null(LinkedInIdTokenValidator.ReadClaims(principal, "nonce-1"));
    }
}
