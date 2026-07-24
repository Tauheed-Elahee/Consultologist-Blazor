using Consultologist.Api.Email;

namespace Consultologist.Api.Tests;

public class EmailAuthenticationResultsTests
{
    // A realistic Exchange Online header shape.
    private const string PassingHeader =
        "spf=pass (sender IP is 209.85.222.41) smtp.mailfrom=gmail.com; " +
        "dkim=pass (signature was verified) header.d=gmail.com; " +
        "dmarc=pass action=none header.from=gmail.com; compauth=pass reason=100";

    [Fact]
    public void Parse_RealisticPassingHeader_Passes()
    {
        var verdict = EmailAuthenticationResults.Parse(PassingHeader);

        Assert.Equal("pass", verdict.Dmarc);
        Assert.Equal("pass", verdict.Spf);
        Assert.Equal("pass", verdict.Dkim);
        Assert.True(verdict.Passes);
    }

    [Fact]
    public void Parse_DmarcPassAlone_Passes()
    {
        var verdict = EmailAuthenticationResults.Parse("spf=softfail smtp.mailfrom=x; dkim=none; dmarc=pass action=none");

        Assert.True(verdict.Passes);
    }

    [Fact]
    public void Parse_SpfAndDkimPass_WithoutDmarc_Passes()
    {
        var verdict = EmailAuthenticationResults.Parse("spf=pass smtp.mailfrom=x; dkim=pass header.d=x");

        Assert.Null(verdict.Dmarc);
        Assert.True(verdict.Passes);
    }

    [Theory]
    [InlineData("spf=pass; dkim=fail header.d=x; dmarc=fail")]
    [InlineData("spf=fail; dkim=pass header.d=x; dmarc=fail")]
    [InlineData("spf=none; dkim=none; dmarc=none")]
    [InlineData("")]
    public void Parse_FailingCombinations_DoNotPass(string header)
    {
        Assert.False(EmailAuthenticationResults.Parse(header).Passes);
    }

    [Fact]
    public void Parse_FirstOccurrencePerMethodWins()
    {
        // A hostile second clause cannot upgrade an earlier fail.
        var verdict = EmailAuthenticationResults.Parse("dmarc=fail action=quarantine; dmarc=pass");

        Assert.Equal("fail", verdict.Dmarc);
        Assert.False(verdict.Passes);
    }

    [Fact]
    public void Evaluate_UsesOnlyTheFirstAuthenticationResultsHeader()
    {
        var headers = new[]
        {
            new GraphInternetMessageHeader("Received", "from somewhere"),
            new GraphInternetMessageHeader("Authentication-Results", "spf=fail; dkim=fail; dmarc=fail"),
            // A forged pre-existing header lower in the list must be ignored.
            new GraphInternetMessageHeader("Authentication-Results", PassingHeader)
        };

        Assert.False(EmailAuthenticationResults.Evaluate(headers).Passes);
    }

    [Fact]
    public void Evaluate_HeaderNameIsCaseInsensitive()
    {
        var headers = new[] { new GraphInternetMessageHeader("authentication-results", PassingHeader) };

        Assert.True(EmailAuthenticationResults.Evaluate(headers).Passes);
    }

    [Fact]
    public void Evaluate_MissingHeader_Fails()
    {
        var headers = new[] { new GraphInternetMessageHeader("Received", "from somewhere") };

        var verdict = EmailAuthenticationResults.Evaluate(headers);

        Assert.Null(verdict.Dmarc);
        Assert.False(verdict.Passes);
    }

    // Intra-tenant mail: no SPF/DKIM/DMARC stamps at all, but Exchange marks
    // the authenticated submission — and EOP strips this header family from
    // external mail, so it cannot be forged from outside.
    [Fact]
    public void Evaluate_AuthenticatedInternal_PassesWithoutAuthenticationResults()
    {
        var headers = new[]
        {
            new GraphInternetMessageHeader("X-MS-Exchange-Organization-AuthAs", "Internal")
        };

        var verdict = EmailAuthenticationResults.Evaluate(headers);

        Assert.True(verdict.AuthenticatedInternal);
        Assert.True(verdict.Passes);
    }

    [Fact]
    public void Evaluate_AuthenticatedInternal_PassesDespiteFailingClauses()
    {
        // Internal mail sometimes carries an Authentication-Results header with
        // none/fail clauses; AuthAs Internal outranks them.
        var headers = new[]
        {
            new GraphInternetMessageHeader("Authentication-Results", "spf=none; dkim=none; dmarc=none"),
            new GraphInternetMessageHeader("x-ms-exchange-organization-authas", "Internal")
        };

        Assert.True(EmailAuthenticationResults.Evaluate(headers).Passes);
    }

    [Theory]
    [InlineData("Anonymous")]
    [InlineData("External")]
    [InlineData("")]
    public void Evaluate_NonInternalAuthAs_DoesNotPass(string authAs)
    {
        var headers = new[]
        {
            new GraphInternetMessageHeader("X-MS-Exchange-Organization-AuthAs", authAs),
            new GraphInternetMessageHeader("Authentication-Results", "spf=none; dkim=none; dmarc=none")
        };

        var verdict = EmailAuthenticationResults.Evaluate(headers);

        Assert.False(verdict.AuthenticatedInternal);
        Assert.False(verdict.Passes);
    }
}
