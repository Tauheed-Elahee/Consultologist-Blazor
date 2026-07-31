using Consultologist.Api.Email;
using Consultologist.Api.Jobs;
using Consultologist.Api.Models;
using Consultologist.Api.Workflow;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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
    private readonly IWorkflowPackagePinResolver _pinResolver = Substitute.For<IWorkflowPackagePinResolver>();
    private readonly IWorkflowPackageStore _packageStore = Substitute.For<IWorkflowPackageStore>();
    private readonly DurableTaskClient _client = Substitute.For<DurableTaskClient>("test");
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-07-25T12:00:00Z"));

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    public EmailIntakeProcessorTests()
    {
        _mail.EnsureInboxChildFolderAsync(Mailbox, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult("folder-" + callInfo.ArgAt<string>(1)));

        // The default: no attachments. Set here rather than in CreateProcessor
        // so a test's own stub is not overwritten by processor construction.
        _mail.ListAttachmentsAsync(Mailbox, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<GraphInboundAttachment>());
    }

    private EmailIntakeProcessor CreateProcessor(
        bool configured = true,
        ILogger<EmailIntakeProcessor>? logger = null)
    {
        var settings = new Dictionary<string, string?>();
        if (configured)
        {
            settings["EmailIntake:MailboxAddress"] = Mailbox;
            settings["EmailIntake:AppBaseUrl"] = "https://app.example.com";
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        return new EmailIntakeProcessor(
            configuration,
            _mail,
            _senderResolver,
            _claims,
            _starter,
            _pinResolver,
            _packageStore,
            _time,
            logger ?? NullLogger<EmailIntakeProcessor>.Instance);
    }

    private static GraphMessageRef Ref(string id = "g-1", string? imid = "<m1@x>") =>
        new(id, imid, DateTimeOffset.Parse("2026-07-25T11:59:00Z"));

    private static GraphMessage Message(
        string id = "g-1",
        string? from = "doc@example.com",
        string? body = "Referral body",
        string authHeader = PassingAuthHeader,
        bool hasAttachments = false)
    {
        return new GraphMessage(
            id,
            "<m1@x>",
            from,
            body,
            new[] { new GraphInternetMessageHeader("Authentication-Results", authHeader) },
            hasAttachments);
    }

    /// <summary>A matched sender and a claimed message — the shape every
    /// attachment test starts from.</summary>
    private void SetupAcceptedSender(GraphMessage message)
    {
        SetupSingleUnread();
        _claims.TryClaimAsync(Arg.Any<EmailIntakeClaim>(), Arg.Any<CancellationToken>()).Returns(true);
        _mail.GetMessageAsync(Mailbox, "g-1", Arg.Any<CancellationToken>()).Returns(message);
        _senderResolver.ResolveAsync("doc@example.com", Arg.Any<CancellationToken>())
            .Returns(new EmailSenderMatch(EmailSenderMatchOutcome.Matched, "user-1"));
        _starter.StartAsync(_client, Arg.Any<ConsultGenerationRequest>(), "user-1",
                Arg.Any<ConsultGenerationJobOrigin>(), Arg.Any<CancellationToken>())
            .Returns(new ConsultGenerationJobStartOutcome("job-1"));
    }

    private void WithAttachments(params GraphInboundAttachment[] attachments) =>
        _mail.ListAttachmentsAsync(Mailbox, "g-1", Arg.Any<CancellationToken>()).Returns(attachments);

    private static GraphInboundAttachment Attachment(string name, string text) =>
        Attachment(name, System.Text.Encoding.UTF8.GetBytes(text));

    // #237: bytes, because email no longer decodes anything. Every fixture
    // used to be UTF-8 by construction, which is why nothing here could catch
    // the mojibake #242 records.
    private static GraphInboundAttachment Attachment(
        string name,
        byte[] content,
        string contentType = "text/plain") =>
        new(name, contentType, content.Length, content);

    private void WithV7Package()
    {
        _pinResolver.ResolvePinAsync("user-1", Arg.Any<CancellationToken>())
            .Returns(new WorkflowPackageRef("general", "latest"));
        _packageStore.ResolveAsync(Arg.Any<WorkflowPackageRef>(), Arg.Any<CancellationToken>())
            .Returns(new WorkflowPackage(V7Fixtures.MultiDeliverable()));
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
                // #210: intake now always supplies the named map — a legacy
                // package's consult_draft entry folds back to the draft in
                // the starter, so the wire shape is one path for both eras.
                Arg.Is<ConsultGenerationRequest>(r =>
                    r.ConsultDraft == null && r.Inputs!["consult_draft"] == "Referral body"),
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
    public async Task InputsMismatch_RecordsTheEligibilityOutcome()
    {
        // v7 email eligibility: a body-only email cannot satisfy a package
        // whose declaration needs more — a distinct claim slug, same disposal.
        SetupSingleUnread();
        _claims.TryClaimAsync(Arg.Any<EmailIntakeClaim>(), Arg.Any<CancellationToken>()).Returns(true);
        _mail.GetMessageAsync(Mailbox, "g-1", Arg.Any<CancellationToken>()).Returns(Message());
        _senderResolver.ResolveAsync("doc@example.com", Arg.Any<CancellationToken>())
            .Returns(new EmailSenderMatch(EmailSenderMatchOutcome.Matched, "user-1"));
        _starter.StartAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(new ConsultGenerationJobStartOutcome(
                null, ConsultGenerationJobStartError.InputsMismatch, "Required input(s) 'labs' missing."));

        var summary = await CreateProcessor().RunOnceAsync(_client, CancellationToken.None);

        Assert.Equal(1, summary.Rejected);
        await _mail.Received(1).MoveMessageAsync(Mailbox, "g-1", "folder-Rejected", Arg.Any<CancellationToken>());
        await _claims.Received(1).UpdateAsync(
            Arg.Is<EmailIntakeClaim>(c => c.Outcome == EmailIntakeOutcomes.RejectedInputs),
            Arg.Any<CancellationToken>());
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
    public async Task UnreadableDocument_IsRecordedAsAnAttachmentProblem()
    {
        // The starter refuses the document (a scan, a corrupt file) after the
        // claim is written. The claim table is the audit surface, so this has
        // to read as an attachment problem — recorded as a generic start
        // failure it would be invisible among registry outages.
        WithV7Package();
        SetupAcceptedSender(Message(hasAttachments: true));
        WithAttachments(Attachment("consult_draft.pdf", "%PDF-1.7 …"u8.ToArray(), "application/pdf"));
        _starter.StartAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(new ConsultGenerationJobStartOutcome(
                null,
                ConsultGenerationJobStartError.InputFileUnreadable,
                "This PDF has no text layer, so it is a scan or a fax."));

        var summary = await CreateProcessor().RunOnceAsync(_client, CancellationToken.None);

        Assert.Equal(1, summary.Rejected);
        await _claims.Received(1).UpdateAsync(
            Arg.Is<EmailIntakeClaim>(c => c.Outcome == EmailIntakeOutcomes.RejectedAttachments),
            Arg.Any<CancellationToken>());
        // And the sender is told which of their attachments to fix, not just
        // that something went wrong.
        await _mail.Received(1).SendMailAsync(
            Mailbox,
            "doc@example.com",
            Arg.Any<string>(),
            Arg.Is<string>(body => body.Contains("no text layer")),
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

    [Fact]
    public async Task BlankBodyWithAttachment_StartsFromTheAttachment()
    {
        // The referral-as-attachment case: a blank-body rejection would have
        // thrown this away. The file fills the draft slot and travels as bytes.
        WithV7Package();
        SetupAcceptedSender(Message(body: "  ", hasAttachments: true));
        WithAttachments(Attachment("referral.txt", "The referral text."));

        var summary = await CreateProcessor().RunOnceAsync(_client, CancellationToken.None);

        Assert.Equal(1, summary.Accepted);
        await _starter.Received(1).StartAsync(
            _client,
            Arg.Is<ConsultGenerationRequest>(r =>
                r.InputFiles!["consult_draft"].Content.Length > 0 && r.Inputs == null),
            "user-1",
            Arg.Any<ConsultGenerationJobOrigin>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PdfAttachment_TravelsToTheParserRatherThanBouncing()
    {
        // The inversion #237 makes: a PDF used to bounce here because this
        // class decided what it could read. It no longer decides — the bytes
        // go to the starter, and the parser rules on them there.
        WithV7Package();
        SetupAcceptedSender(Message(body: "Please see attached.", hasAttachments: true));
        WithAttachments(Attachment("consult_draft.pdf", "%PDF-1.7 …"u8.ToArray(), "application/pdf"));

        var summary = await CreateProcessor().RunOnceAsync(_client, CancellationToken.None);

        Assert.Equal(1, summary.Accepted);
        await _starter.Received(1).StartAsync(
            _client,
            Arg.Is<ConsultGenerationRequest>(r =>
                r.InputFiles!["consult_draft"].ContentType == "application/pdf"),
            "user-1",
            Arg.Any<ConsultGenerationJobOrigin>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Utf16Attachment_ReachesTheStarterByteForByte()
    {
        // #242's regression test, and the shape of its fix: the defect was
        // this class decoding as UTF-8 and substituting U+FFFD. It cannot
        // mangle what it never reads, so the assertion is that the bytes
        // arrive unchanged and the parser decides how to read them.
        var utf16 = System.Text.Encoding.Unicode.GetPreamble()
            .Concat(System.Text.Encoding.Unicode.GetBytes("Résumé of prior notes — see attached."))
            .ToArray();

        WithV7Package();
        SetupAcceptedSender(Message(hasAttachments: true));
        WithAttachments(Attachment("prior_notes.txt", utf16));

        await CreateProcessor().RunOnceAsync(_client, CancellationToken.None);

        await _starter.Received(1).StartAsync(
            _client,
            Arg.Is<ConsultGenerationRequest>(r => r.InputFiles!["prior_notes"].Content.SequenceEqual(utf16)),
            "user-1",
            Arg.Any<ConsultGenerationJobOrigin>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LegacyPackageAttachment_IsRefusedAndTheReplySaysWhy()
    {
        // A package declaring no inputs has one implicit slot, so a file has
        // nowhere of its own to go. The sender is told that, rather than
        // getting the generic bounce.
        SetupAcceptedSender(Message(hasAttachments: true));
        WithAttachments(Attachment("referral.txt", "The referral."));

        var summary = await CreateProcessor().RunOnceAsync(_client, CancellationToken.None);

        Assert.Equal(1, summary.Rejected);
        await _starter.DidNotReceiveWithAnyArgs().StartAsync(default!, default!, default!, default!, default);
        await _mail.Received(1).SendMailAsync(
            Mailbox,
            "doc@example.com",
            Arg.Any<string>(),
            Arg.Is<string>(body => body.Contains("accepts a single input")),
            Arg.Any<CancellationToken>());
    }

    // ---- the logging audit (#241, § 9) ----------------------------------

    [Fact]
    public async Task ARejectedAttachment_PutsNeitherItsNameNorItsContentInTheLog()
    {
        // This is the only door that has a filename at all — InputFilePayload
        // carries content and a content type and nothing else — so it is the
        // only place the "no filename in logs" half of § 9 can be violated.
        //
        // The rejection paths are the ones worth pinning: they log
        // Detail={Detail}, which carries either an attachment size message or
        // a reject reason. Reject reasons name input SLOTS ("consult_draft"),
        // never files, and Application Insights stores those template values
        // as customDimensions — so the assertion is over the structured
        // values, not just the rendered message.
        const string FileName = "SENTINEL-FILENAME-4a5b6c.txt";
        const string Content = "SENTINEL-CLINICAL-CONTENT-0f1e2d";

        var log = new CapturingLogger<EmailIntakeProcessor>();
        WithV7Package();
        SetupAcceptedSender(Message(body: "  ", hasAttachments: true));
        WithAttachments(Attachment(FileName, Content), Attachment("b.txt", Content));

        var summary = await CreateProcessor(logger: log).RunOnceAsync(_client, CancellationToken.None);

        Assert.Equal(1, summary.Rejected);
        Assert.DoesNotContain(FileName, log.Everything, StringComparison.Ordinal);
        Assert.DoesNotContain(Content, log.Everything, StringComparison.Ordinal);

        // The log is not empty — otherwise this passes for the wrong reason,
        // which is the failure mode an absence check always has.
        Assert.NotEmpty(log.Recorded);
    }

    [Fact]
    public async Task AmbiguousAttachments_AreRejectedRatherThanGuessed()
    {
        // Two unnamed files with nowhere unambiguous to go. The reply cannot
        // say where they went, so a guess would be silent wrong data.
        WithV7Package();
        SetupAcceptedSender(Message(body: "  ", hasAttachments: true));
        WithAttachments(Attachment("a.txt", "One."), Attachment("b.txt", "Two."));

        var summary = await CreateProcessor().RunOnceAsync(_client, CancellationToken.None);

        Assert.Equal(1, summary.Rejected);
        await _starter.DidNotReceiveWithAnyArgs().StartAsync(default!, default!, default!, default!, default);
        await _claims.Received(1).UpdateAsync(
            Arg.Is<EmailIntakeClaim>(c => c.Outcome == EmailIntakeOutcomes.RejectedAttachments),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NamedAttachment_FillsItsDeclaredSlot()
    {
        WithV7Package();
        SetupAcceptedSender(Message(hasAttachments: true));
        WithAttachments(Attachment("prior_notes.txt", "Old records."));

        var summary = await CreateProcessor().RunOnceAsync(_client, CancellationToken.None);

        Assert.Equal(1, summary.Accepted);
        await _starter.Received(1).StartAsync(
            _client,
            // The body is text, the attachment is bytes — the same split the
            // Consults page now sends.
            Arg.Is<ConsultGenerationRequest>(r =>
                r.Inputs!["consult_draft"] == "Referral body"
                && r.InputFiles!.ContainsKey("prior_notes")),
            "user-1",
            Arg.Any<ConsultGenerationJobOrigin>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MessageWithoutAttachments_NeverCallsGraphForThem()
    {
        SetupAcceptedSender(Message());

        await CreateProcessor().RunOnceAsync(_client, CancellationToken.None);

        await _mail.DidNotReceiveWithAnyArgs().ListAttachmentsAsync(default!, default!, default);
    }
}
