using Consultologist.Api.Auth;

namespace Consultologist.Api.Tests;

public class AccountStoreLinkOutcomeTests
{
    [Fact]
    public void DecideLinkOutcome_NoExistingLink_Links()
    {
        Assert.Equal(IdentityLinkOutcome.Linked, AccountStore.DecideLinkOutcome(null, "user-1"));
    }

    [Fact]
    public void DecideLinkOutcome_SameUser_IsIdempotent()
    {
        var existing = new IdentityLinkEntity { AppUserId = "user-1" };

        Assert.Equal(IdentityLinkOutcome.AlreadyLinkedToSelf, AccountStore.DecideLinkOutcome(existing, "user-1"));
    }

    [Fact]
    public void DecideLinkOutcome_OtherUser_Conflicts()
    {
        var existing = new IdentityLinkEntity { AppUserId = "user-2" };

        Assert.Equal(IdentityLinkOutcome.ConflictOtherUser, AccountStore.DecideLinkOutcome(existing, "user-1"));
    }

    [Fact]
    public void CreateSubjectHash_IsStableAndNamespacedByProvider()
    {
        var linkedIn = AccountStore.CreateSubjectHash("linkedin", "https://www.linkedin.com/oauth", "abc123");

        Assert.Equal(linkedIn, AccountStore.CreateSubjectHash("linkedin", "https://www.linkedin.com/oauth", "abc123"));
        Assert.NotEqual(linkedIn, AccountStore.CreateSubjectHash("entra-external-id", "https://www.linkedin.com/oauth", "abc123"));
        Assert.DoesNotContain(linkedIn, c => c is '+' or '/' or '=');
    }
}
