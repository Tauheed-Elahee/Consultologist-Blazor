using Consultologist.Api;
using Consultologist.Api.Auth;

namespace Consultologist.Api.Tests;

public class AccountLinkedInCallbackQueryTests
{
    [Fact]
    public void ParseCallbackQuery_ExtractsCodeAndState()
    {
        var url = new Uri("https://api.example.com/api/Account/LinkedIn/Callback?code=abc%2F123&state=st-1");

        var (code, state, error) = AccountLinkedIn.ParseCallbackQuery(url);

        Assert.Equal("abc/123", code);
        Assert.Equal("st-1", state);
        Assert.Null(error);
    }

    [Fact]
    public void ParseCallbackQuery_ExtractsProviderError()
    {
        var url = new Uri("https://api.example.com/cb?error=user_cancelled_login&error_description=x&state=st-1");

        var (code, state, error) = AccountLinkedIn.ParseCallbackQuery(url);

        Assert.Null(code);
        Assert.Equal("st-1", state);
        Assert.Equal("user_cancelled_login", error);
    }

    [Fact]
    public void ParseCallbackQuery_IgnoresEmptyValuesAndUnknownKeys()
    {
        var url = new Uri("https://api.example.com/cb?code=&other=1&state=st-1");

        var (code, state, error) = AccountLinkedIn.ParseCallbackQuery(url);

        Assert.Null(code);
        Assert.Equal("st-1", state);
        Assert.Null(error);
    }

    [Fact]
    public void ParseCallbackQuery_EmptyQuery_AllNull()
    {
        var (code, state, error) = AccountLinkedIn.ParseCallbackQuery(new Uri("https://api.example.com/cb"));

        Assert.Null(code);
        Assert.Null(state);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(LinkedInCallbackOutcome.Denied, "denied")]
    [InlineData(LinkedInCallbackOutcome.ExchangeFailed, "exchange-failed")]
    [InlineData(LinkedInCallbackOutcome.TokenInvalid, "token-invalid")]
    [InlineData(LinkedInCallbackOutcome.AlreadyLinked, "already-linked")]
    public void ReasonSlug_MapsOutcomes(LinkedInCallbackOutcome outcome, string expected)
    {
        Assert.Equal(expected, AccountLinkedIn.ReasonSlug(outcome));
    }
}
