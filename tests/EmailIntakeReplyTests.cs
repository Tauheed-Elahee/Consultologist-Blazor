using Consultologist.Api.Email;

namespace Consultologist.Api.Tests;

public class EmailIntakeReplyTests
{
    [Fact]
    public void Compose_Completed_HasFixedSubjectAndDeepLink()
    {
        var (subject, body) = EmailIntakeReply.Compose("https://app.example.com", "job-1", "Completed");

        Assert.Equal("Your consult is ready", subject);
        Assert.Contains("https://app.example.com/history/job-1", body);
        Assert.Contains("no clinical content", body);
    }

    [Fact]
    public void Compose_Failed_HasFixedSubjectAndDeepLink()
    {
        var (subject, body) = EmailIntakeReply.Compose("https://app.example.com", "job-1", "Failed");

        Assert.Equal("Your consult run did not complete", subject);
        Assert.Contains("https://app.example.com/history/job-1", body);
    }

    [Fact]
    public void Compose_TrailingSlashBaseUrl_ProducesCleanLink()
    {
        var (_, body) = EmailIntakeReply.Compose("https://app.example.com/", "job-1", "Completed");

        Assert.Contains("https://app.example.com/history/job-1", body);
        Assert.DoesNotContain("com//history", body);
    }

    [Fact]
    public void Compose_WithAttachment_MentionsTheEncryptedDocument()
    {
        var (subject, body) = EmailIntakeReply.Compose(
            "https://app.example.com", "job-1", "Completed", includesAttachment: true);

        Assert.Equal("Your consult is ready", subject);
        Assert.Contains("encrypted with your delivery password", body);
        Assert.Contains("https://app.example.com/history/job-1", body);
    }

    [Fact]
    public void Compose_WithoutAttachment_DoesNotMentionOne()
    {
        var (_, body) = EmailIntakeReply.Compose("https://app.example.com", "job-1", "Completed");

        Assert.DoesNotContain("attached", body);
    }

    [Fact]
    public void Compose_NeverEchoesCallerContent()
    {
        // The only caller-varying inputs are the base URL and job id; a hostile
        // job id must appear only inside the link, and inbound subject/body
        // have no path into Compose at all.
        var hostile = "job<script>alert(1)</script>";
        var (subject, body) = EmailIntakeReply.Compose("https://app.example.com", hostile, "Completed");

        Assert.Equal("Your consult is ready", subject);
        Assert.Contains($"/history/{hostile}", body);
    }
}
