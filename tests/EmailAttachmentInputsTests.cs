using Consultologist.Api.Email;

namespace Consultologist.Api.Tests;

/// <summary>
/// Where an email's body and attachments land (#210). The rule refuses
/// ambiguity rather than guessing, because a no-PHI reply can never tell the
/// sender which file went where.
/// </summary>
public class EmailAttachmentInputsTests
{
    private static readonly string[] TwoSlots = { "consult_draft", "prior_notes" };

    // #237: the bytes are never read here — the parser reads them at job
    // start. The text is only so a reader can see what a fixture stands for.
    private static EmailInputAttachment File(string name, string text) =>
        new(name, "text/plain", System.Text.Encoding.UTF8.GetBytes(text));

    private static EmailAttachmentInputs.Resolution Resolve(
        IReadOnlyList<string> slots,
        string? body,
        params EmailInputAttachment[] attachments) =>
        EmailAttachmentInputs.Resolve(slots, body, attachments);

    [Fact]
    public void BodyOnly_FillsTheDraftSlot()
    {
        var result = Resolve(TwoSlots, "Referral body.");

        Assert.Null(result.RejectReason);
        Assert.Equal(new Dictionary<string, string> { ["consult_draft"] = "Referral body." }, result.Inputs);
        Assert.Empty(result.Files!);
    }

    [Fact]
    public void FilenameStem_ClaimsItsSlotRegardlessOfOrder()
    {
        var result = Resolve(TwoSlots, "Referral body.", File("prior_notes.txt", "Old records."));

        Assert.Null(result.RejectReason);
        Assert.Equal(new Dictionary<string, string> { ["consult_draft"] = "Referral body." }, result.Inputs);
        Assert.Equal("prior_notes.txt", result.Files!["prior_notes"].FileName);
    }

    [Fact]
    public void StemMatch_IsCaseInsensitiveAndIgnoresExtension()
    {
        var result = Resolve(TwoSlots, "Body.", File("Prior_Notes.MD", "Records."));

        Assert.Null(result.RejectReason);
        Assert.Equal("Prior_Notes.MD", result.Files!["prior_notes"].FileName);
    }

    [Fact]
    public void NamedAttachment_OutranksTheBodyForItsSlot()
    {
        // Most mail clients append a signature, so a sender who types nothing
        // still produces a body. It must not compete with a file they
        // deliberately named — this combination used to reject outright.
        var result = Resolve(
            TwoSlots,
            "Dr. Lee | Oncology | Clinic",
            File("consult_draft.txt", "The referral."),
            File("prior_notes.txt", "Old records."));

        Assert.Null(result.RejectReason);
        Assert.Equal("consult_draft.txt", result.Files!["consult_draft"].FileName);
        Assert.Equal("prior_notes.txt", result.Files["prior_notes"].FileName);
        // The body lost the slot it would otherwise have taken.
        Assert.Empty(result.Inputs!);
    }

    [Fact]
    public void NamedDraftAttachment_WinsEvenWhenItIsTheOnlyFile()
    {
        var result = Resolve(TwoSlots, "Please see the attached referral.", File("consult_draft.md", "The referral."));

        Assert.Null(result.RejectReason);
        Assert.Equal("consult_draft.md", result.Files!["consult_draft"].FileName);
        Assert.False(result.Files.ContainsKey("prior_notes"));
        Assert.Empty(result.Inputs!);
    }

    [Fact]
    public void OneUnnamedAttachment_FillsTheOneFreeSlot()
    {
        // The ordinary case: body is the referral, the file is whatever else
        // the package asked for.
        var result = Resolve(TwoSlots, "Referral body.", File("scan001.txt", "Old records."));

        Assert.Null(result.RejectReason);
        Assert.Equal("scan001.txt", result.Files!["prior_notes"].FileName);
        Assert.Equal("Referral body.", result.Inputs!["consult_draft"]);
    }

    [Fact]
    public void BlankBody_LetsTheAttachmentBecomeTheDraft()
    {
        // The referral-as-attachment shape, and what a fax bridge will look
        // like once PDFs are readable.
        var result = Resolve(TwoSlots, "   ", File("fax_20260728.txt", "The referral."));

        Assert.Null(result.RejectReason);
        Assert.Equal("fax_20260728.txt", result.Files!["consult_draft"].FileName);
        Assert.False(result.Files.ContainsKey("prior_notes"));
    }

    [Fact]
    public void TwoUnnamedAttachments_AreRefusedRatherThanGuessed()
    {
        // Two files, two free slots, and no way to confirm the assignment back
        // to the sender — a swap here would be silent wrong data.
        var result = Resolve(TwoSlots, null, File("a.txt", "One."), File("b.txt", "Two."));

        Assert.Null(result.Inputs);
        Assert.Contains("Name each file", result.RejectReason);
    }

    [Fact]
    public void MoreAttachmentsThanSlots_AreRefused()
    {
        var result = Resolve(
            TwoSlots,
            "Referral body.",
            File("a.txt", "One."),
            File("b.txt", "Two."));

        Assert.Null(result.Inputs);
        Assert.Contains("More attachments", result.RejectReason);
    }

    [Fact]
    public void TwoFilesForOneSlot_AreRefused()
    {
        var result = Resolve(
            TwoSlots,
            "Body.",
            File("prior_notes.txt", "One."),
            File("prior_notes.md", "Two."));

        Assert.Null(result.Inputs);
        Assert.Contains("More than one input", result.RejectReason);
    }

    [Fact]
    public void NothingUsable_IsRefused()
    {
        var result = Resolve(TwoSlots, "  ");

        Assert.Null(result.Inputs);
        Assert.Contains("neither a usable body nor an attachment", result.RejectReason);
    }

    [Fact]
    public void LegacyPackage_RefusesAnAttachment()
    {
        // v5/v6 declare no slots, so one implicit slot and nowhere for a file
        // to go. This used to concatenate the attachment into the body, which
        // only worked while email decoded files itself (#237).
        var result = Resolve(Array.Empty<string>(), "Referral body.", File("extra.txt", "Old records."));

        Assert.Null(result.Inputs);
        Assert.Contains("accepts a single input", result.RejectReason);
    }

    [Fact]
    public void LegacyPackage_BodyOnly_StillWorks()
    {
        var result = Resolve(Array.Empty<string>(), "The referral.", Array.Empty<EmailInputAttachment>());

        Assert.Null(result.RejectReason);
        Assert.Equal("The referral.", result.Inputs!["consult_draft"]);
    }
}
