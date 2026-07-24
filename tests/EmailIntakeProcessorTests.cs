using Consultologist.Api.Email;
using Consultologist.Api.Jobs;
using Consultologist.Api.Models;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Consultologist.Api.Tests;

public class EmailIntakeProcessorTests
{
    private const string Mailbox = "consults@example.com";
    private const string PassingAuthHeader = "spf=pass smtp.mailfrom=x; dkim=pass header.d=x; dmarc=pass action=none";

    private readonly IGraphMailClient _mail = Substitute.For<IGraphMailClient>();
    private readonly IEmailSenderResolver _senderResolver = Substitute.For<IEmailSenderResolver>();
    private readonly IEmailIntakeClaimStore _claims = Substitute.For<IEmailIntakeClaimStore>();
    private readonly IConsultGenerationJobStarter _starter = Substitute.For<IConsultGenerationJobStarter>();
    private readonly DurableTaskClient _client = Substitute.For<DurableTaskClient>("test");
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-07-25T12:00:00Z"));

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private EmailIntakeProcessor CreateProcessor(bool configured = true)
    {
        var settings = new Dictionary<string, string?>();
        if (configured)
        {
            settings["EmailIntake:MailboxAddress"] = Mailbox;
            settings["EmailIntake:AppBaseUrl"] = "https://app.example.com";
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        _mail.EnsureInboxChildFolderAsync(Mailbox, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult("folder-" + callInfo.ArgAt<string>(1)));

        return new EmailIntakeProcessor(
            configuration,
            _mail,
            _senderResolver,
            _claims,
            _starter,
            _time,
            NullLogger<EmailIntakeProcessor>.Instance);
    }

    private static GraphMessageRef Ref(string id = "g-1", string? imid = "<m1@x>") =>
        new(id, imid, DateTimeOffset.Parse("2026-07-25T11:59:00Z"));

    private static GraphMessage Message(
        string id = "g-1",
        string? from = "doc@example.com",
        string? body = "Referral body",
        string authHeader = PassingAuthHeader)
    {
        return new GraphMessage(
            id,
            "<m1@x>",
            from,
            body,
            new[] { new GraphInternetMessageHeader("Authentication-Results", authHeader) });
    }

    private void SetupSingleUnread()
    {
        _mail.ListUnreadInboxMessagesAsync(Mailbox, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { Ref() });
    }

    [Fact]
    public async Task MailboxUnset_NoGraphCalls()
    {
        var summary = await CreateProcessor(configured: false).RunOnceAsync(_client, CancellationToken.None);

        Assert.Equal(0, summary.Listed);
        await _mail.DidNotReceiveWithAnyArgs().ListUnreadInboxMessagesAsync(default!, default, default);
    }

    [Fact]
    public async Task HappyPath_ClaimsBeforeStartAndDisposesToProcessed()
    {
        SetupSingleUnread();
        _claims.TryClaimAsync(Arg.Any<EmailIntakeClaim>(), Arg.Any<CancellationToken>()).Returns(true);
        _mail.GetMessageAsync(Mailbox, "g-1", Arg.Any<CancellationToken>()).Returns(Message());
        _senderResolver.ResolveAsync("doc@example.com", Arg.Any<CancellationToken>())
            .Returns(new EmailSenderMatch(EmailSenderMatchOutcome.Matched, "user-1"));
        _starter.StartAsync(
                _client,
                Arg.Any<ConsultGenerationRequest>(),
                "user-1",
                Arg.Any<ConsultGenerationJobOrigin>(),
                Arg.Any<CancellationToken>())
            .Returns(new ConsultGenerationJobStartOutcome("job-1"));

        var summary = await CreateProcessor().RunOnceAsync(_client, CancellationToken.None);

        Assert.Equal(1, summary.Accepted);
        Received.InOrder(() =>
        {
            _claims.TryClaimAsync(Arg.Is<EmailIntakeClaim>(c => c.ClaimKey == "<m1@x>"), Arg.Any<CancellationToken>());
            _starter.StartAsync(
                _client,
                Arg.Is<ConsultGenerationRequest>(r => r.ConsultDraft == "Referral body"),
                "user-1",
                Arg.Is<ConsultGenerationJobOrigin>(o =>
                    o.Source == ConsultGenerationJobSources.Email && o.ReplyToAddress == "doc@example.com"),
                Arg.Any<CancellationToken>());
        });
        await _mail.Received(1).MoveMessageAsync(Mailbox, "g-1", "folder-Processed", Arg.Any<CancellationToken>());
        await _claims.Received(1).UpdateAsync(
            Arg.Is<EmailIntakeClaim>(c => c.JobId == "job-1" && c.Outcome == EmailIntakeOutcomes.Accepted && c.AppUserId == "user-1"),
            Arg.Any<CancellationToken>());
        await _mail.DidNotReceiveWithAnyArgs().SendMailAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task ClaimLost_TerminalClaim_RepairsDispositionWithoutStart()
    {
        SetupSingleUnread();
        _claims.TryClaimAsync(Arg.Any<EmailIntakeClaim>(), Arg.Any<CancellationToken>()).Returns(false);
        _claims.GetAsync("<m1@x>", Arg.Any<CancellationToken>())
            .Returns(new EmailIntakeClaim("<m1@x>", "g-1", null, _time.GetUtcNow().AddMinutes(-1), "user-1", "job-1", EmailIntakeOutcomes.Accepted));

        var summary = await CreateProcessor().RunOnceAsync(_client, CancellationToken.None);

        Assert.Equal(1, summary.Repaired);
        await _mail.Received(1).MoveMessageAsync(Mailbox, "g-1", "folder-Processed", Arg.Any<CancellationToken>());
        await _starter.DidNotReceiveWithAnyArgs().StartAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task ClaimLost_YoungPendingClaim_SkipsEntirely()
    {
        SetupSingleUnread();
        _claims.TryClaimAsync(Arg.Any<EmailIntakeClaim>(), Arg.Any<CancellationToken>()).Returns(false);
        _claims.GetAsync("<m1@x>", Arg.Any<CancellationToken>())
            .Returns(new EmailIntakeClaim("<m1@x>", "g-1", null, _time.GetUtcNow().AddMinutes(-2)));

        var summary = await CreateProcessor().RunOnceAsync(_client, CancellationToken.None);

        Assert.Equal(1, summary.Skipped);
        await _mail.DidNotReceiveWithAnyArgs().MoveMessageAsync(default!, default!, default!, default);
        await _starter.DidNotReceiveWithAnyArgs().StartAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task ClaimLost_StaleOutcomelessClaim_RepairsToRejected()
    {
        SetupSingleUnread();
        _claims.TryClaimAsync(Arg.Any<EmailIntakeClaim>(), Arg.Any<CancellationToken>()).Returns(false);
        _claims.GetAsync("<m1@x>", Arg.Any<CancellationToken>())
            .Returns(new EmailIntakeClaim("<m1@x>", "g-1", null, _time.GetUtcNow().AddMinutes(-30)));

        var summary = await CreateProcessor().RunOnceAsync(_client, CancellationToken.None);

        Assert.Equal(1, summary.Repaired);
        await _mail.Received(1).MoveMessageAsync(Mailbox, "g-1", "folder-Rejected", Arg.Any<CancellationToken>());
        await _starter.DidNotReceiveWithAnyArgs().StartAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task AuthFloorFailure_RejectsSilently()
    {
        SetupSingleUnread();
        _claims.TryClaimAsync(Arg.Any<EmailIntakeClaim>(), Arg.Any<CancellationToken>()).Returns(true);
        _mail.GetMessageAsync(Mailbox, "g-1", Arg.Any<CancellationToken>())
            .Returns(Message(authHeader: "spf=fail; dkim=fail; dmarc=fail"));

        var summary = await CreateProcessor().RunOnceAsync(_client, CancellationToken.None);

        Assert.Equal(1, summary.Rejected);
        await _mail.Received(1).MoveMessageAsync(Mailbox, "g-1", "folder-Rejected", Arg.Any<CancellationToken>());
        await _claims.Received(1).UpdateAsync(
            Arg.Is<EmailIntakeClaim>(c => c.Outcome == EmailIntakeOutcomes.RejectedAuth),
            Arg.Any<CancellationToken>());
        await _senderResolver.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default);
        await _starter.DidNotReceiveWithAnyArgs().StartAsync(default!, default!, default!, default!, default);
        await _mail.DidNotReceiveWithAnyArgs().SendMailAsync(default!, default!, default!, default!, default);
    }

    [Theory]
    [InlineData(EmailSenderMatchOutcome.NoMatch)]
    [InlineData(EmailSenderMatchOutcome.Ambiguous)]
    [InlineData(EmailSenderMatchOutcome.NotActive)]
    public async Task SenderGateFailure_RejectsSilently(EmailSenderMatchOutcome outcome)
    {
        SetupSingleUnread();
        _claims.TryClaimAsync(Arg.Any<EmailIntakeClaim>(), Arg.Any<CancellationToken>()).Returns(true);
        _mail.GetMessageAsync(Mailbox, "g-1", Arg.Any<CancellationToken>()).Returns(Message());
        _senderResolver.ResolveAsync("doc@example.com", Arg.Any<CancellationToken>())
            .Returns(new EmailSenderMatch(outcome));

        var summary = await CreateProcessor().RunOnceAsync(_client, CancellationToken.None);

        Assert.Equal(1, summary.Rejected);
        await _claims.Received(1).UpdateAsync(
            Arg.Is<EmailIntakeClaim>(c => c.Outcome == EmailIntakeOutcomes.RejectedSender),
            Arg.Any<CancellationToken>());
        await _starter.DidNotReceiveWithAnyArgs().StartAsync(default!, default!, default!, default!, default);
        await _mail.DidNotReceiveWithAnyArgs().SendMailAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task BlankBody_RejectsSilently()
    {
        SetupSingleUnread();
        _claims.TryClaimAsync(Arg.Any<EmailIntakeClaim>(), Arg.Any<CancellationToken>()).Returns(true);
        _mail.GetMessageAsync(Mailbox, "g-1", Arg.Any<CancellationToken>()).Returns(Message(body: "   "));
        _senderResolver.ResolveAsync("doc@example.com", Arg.Any<CancellationToken>())
            .Returns(new EmailSenderMatch(EmailSenderMatchOutcome.Matched, "user-1"));

        var summary = await CreateProcessor().RunOnceAsync(_client, CancellationToken.None);

        Assert.Equal(1, summary.Rejected);
        await _claims.Received(1).UpdateAsync(
            Arg.Is<EmailIntakeClaim>(c => c.Outcome == EmailIntakeOutcomes.RejectedEmpty),
            Arg.Any<CancellationToken>());
        await _starter.DidNotReceiveWithAnyArgs().StartAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task StarterError_RejectsAndSendsOneGenericReply()
    {
        SetupSingleUnread();
        _claims.TryClaimAsync(Arg.Any<EmailIntakeClaim>(), Arg.Any<CancellationToken>()).Returns(true);
        _mail.GetMessageAsync(Mailbox, "g-1", Arg.Any<CancellationToken>()).Returns(Message());
        _senderResolver.ResolveAsync("doc@example.com", Arg.Any<CancellationToken>())
            .Returns(new EmailSenderMatch(EmailSenderMatchOutcome.Matched, "user-1"));
        _starter.StartAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(new ConsultGenerationJobStartOutcome(null, ConsultGenerationJobStartError.RegistryUnavailable, "down"));

        var summary = await CreateProcessor().RunOnceAsync(_client, CancellationToken.None);

        Assert.Equal(1, summary.Rejected);
        await _mail.Received(1).MoveMessageAsync(Mailbox, "g-1", "folder-Rejected", Arg.Any<CancellationToken>());
        await _claims.Received(1).UpdateAsync(
            Arg.Is<EmailIntakeClaim>(c => c.Outcome == EmailIntakeOutcomes.StartFailed),
            Arg.Any<CancellationToken>());
        await _mail.Received(1).SendMailAsync(
            Mailbox,
            "doc@example.com",
            Arg.Is<string>(s => !s.Contains("Referral")),
            Arg.Is<string>(b => !b.Contains("Referral body")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VanishedMessage_RecordsOutcomeAndSkips()
    {
        SetupSingleUnread();
        _claims.TryClaimAsync(Arg.Any<EmailIntakeClaim>(), Arg.Any<CancellationToken>()).Returns(true);
        _mail.GetMessageAsync(Mailbox, "g-1", Arg.Any<CancellationToken>()).Returns((GraphMessage?)null);

        var summary = await CreateProcessor().RunOnceAsync(_client, CancellationToken.None);

        Assert.Equal(1, summary.Skipped);
        await _claims.Received(1).UpdateAsync(
            Arg.Is<EmailIntakeClaim>(c => c.Outcome == EmailIntakeOutcomes.Vanished),
            Arg.Any<CancellationToken>());
    }
}
