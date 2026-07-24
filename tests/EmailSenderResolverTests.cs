using Consultologist.Api.Email;

namespace Consultologist.Api.Tests;

public class EmailSenderResolverTests
{
    private static (string, string?, string) User(string id, string? email, string status = "Active") =>
        (id, email, status);

    [Fact]
    public void Decide_NoUsers_NoMatch()
    {
        var match = TableEmailSenderResolver.Decide(
            new List<(string, string?, string)>(),
            "doc@example.com");

        Assert.Equal(EmailSenderMatchOutcome.NoMatch, match.Outcome);
        Assert.Null(match.AppUserId);
    }

    [Fact]
    public void Decide_SingleActiveMatch_Matches()
    {
        var match = TableEmailSenderResolver.Decide(
            new List<(string, string?, string)>
            {
                User("user-1", "doc@example.com"),
                User("user-2", "other@example.com")
            },
            "doc@example.com");

        Assert.Equal(EmailSenderMatchOutcome.Matched, match.Outcome);
        Assert.Equal("user-1", match.AppUserId);
    }

    [Fact]
    public void Decide_NormalizesCaseAndWhitespace()
    {
        var match = TableEmailSenderResolver.Decide(
            new List<(string, string?, string)> { User("user-1", "Doc@Example.com ") },
            "  doc@example.COM");

        Assert.Equal(EmailSenderMatchOutcome.Matched, match.Outcome);
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Disabled")]
    public void Decide_SingleNonActiveMatch_IsNotActive(string status)
    {
        var match = TableEmailSenderResolver.Decide(
            new List<(string, string?, string)> { User("user-1", "doc@example.com", status) },
            "doc@example.com");

        Assert.Equal(EmailSenderMatchOutcome.NotActive, match.Outcome);
        Assert.Null(match.AppUserId);
    }

    [Fact]
    public void Decide_TwoAccountsSameEmail_IsAmbiguous()
    {
        var match = TableEmailSenderResolver.Decide(
            new List<(string, string?, string)>
            {
                User("user-1", "doc@example.com"),
                User("user-2", "doc@example.com", "Pending")
            },
            "doc@example.com");

        Assert.Equal(EmailSenderMatchOutcome.Ambiguous, match.Outcome);
    }

    [Fact]
    public void Decide_SameAccountListedTwice_IsNotAmbiguous()
    {
        var match = TableEmailSenderResolver.Decide(
            new List<(string, string?, string)>
            {
                User("user-1", "doc@example.com"),
                User("user-1", "doc@example.com")
            },
            "doc@example.com");

        Assert.Equal(EmailSenderMatchOutcome.Matched, match.Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Decide_BlankFromAddress_NoMatch(string from)
    {
        var match = TableEmailSenderResolver.Decide(
            new List<(string, string?, string)> { User("user-1", null) },
            from);

        Assert.Equal(EmailSenderMatchOutcome.NoMatch, match.Outcome);
    }

    [Fact]
    public void CreateRowKey_IsBase64UrlAndStable()
    {
        var key = TableEmailIntakeClaimStore.CreateRowKey("<abc/123#x?y@mail.example.com>");

        Assert.Equal(key, TableEmailIntakeClaimStore.CreateRowKey("<abc/123#x?y@mail.example.com>"));
        Assert.DoesNotContain(key, c => c is '/' or '\\' or '#' or '?' or '+' or '=');
    }
}
