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

    private static EmailInputAttachment File(string name, string text) => new(name, text);

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
    }

    [Fact]
    public void FilenameStem_ClaimsItsSlotRegardlessOfOrder()
    {
        var result = Resolve(TwoSlots, "Referral body.", File("prior_notes.txt", "Old records."));

        Assert.Null(result.RejectReason);
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["consult_draft"] = "Referral body.",
                ["prior_notes"] = "Old records."
            },
            result.Inputs);
    }

    [Fact]
    public void StemMatch_IsCaseInsensitiveAndIgnoresExtension()
    {
        var result = Resolve(TwoSlots, "Body.", File("Prior_Notes.MD", "Records."));

        Assert.Null(result.RejectReason);
        Assert.Equal("Records.", result.Inputs!["prior_notes"]);
    }

    [Fact]
    public void OneUnnamedAttachment_FillsTheOneFreeSlot()
    {
        // The ordinary case: body is the referral, the file is whatever else
        // the package asked for.
        var result = Resolve(TwoSlots, "Referral body.", File("scan001.txt", "Old records."));

        Assert.Null(result.RejectReason);
        Assert.Equal("Old records.", result.Inputs!["prior_notes"]);
    }

    [Fact]
    public void BlankBody_LetsTheAttachmentBecomeTheDraft()
    {
        // The referral-as-attachment shape, and what a fax bridge will look
        // like once PDFs are readable.
        var result = Resolve(TwoSlots, "   ", File("fax_20260728.txt", "The referral."));

        Assert.Null(result.RejectReason);
        Assert.Equal("The referral.", result.Inputs!["consult_draft"]);
        Assert.False(result.Inputs.ContainsKey("prior_notes"));
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
    public void LegacyPackage_AppendsAttachmentsToTheBody()
    {
        // v5/v6 declare no slots, so positional has nowhere to go.
        var result = Resolve(Array.Empty<string>(), "Referral body.", File("extra.txt", "Old records."));

        Assert.Null(result.RejectReason);
        Assert.Equal("Referral body.\n\nOld records.", result.Inputs!["consult_draft"]);
    }

    [Fact]
    public void LegacyPackage_BlankBody_UsesTheAttachmentAlone()
    {
        var result = Resolve(Array.Empty<string>(), null, File("referral.txt", "The referral."));

        Assert.Null(result.RejectReason);
        Assert.Equal("The referral.", result.Inputs!["consult_draft"]);
    }
}
