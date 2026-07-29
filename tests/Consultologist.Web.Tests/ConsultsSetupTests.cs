using AngleSharp.Dom;
using Bunit;
using Consultologist.Web.Pages;
using Consultologist.Web.Services.AI;
using Consultologist.Web.Services.Documents;
using Consultologist.Web.Services.Workflow;
using NSubstitute;

namespace Consultologist.Web.Tests;

/// <summary>
/// The setup form's render (#224). The first test here is the regression for
/// #223: a bind expression Blazor can only reject when the component renders
/// took the whole form down in production, for every spec version.
/// </summary>
public class ConsultsSetupTests : ClientRenderTestContext
{
    private static IReadOnlyList<WorkflowPackageBlockResponse> NineSections(string prefix = "") =>
        new[] { "hpi", "pmh", "medications", "allergies", "social_history", "family_history", "exam", "investigations", "assessment_plan" }
            .Select(id => Block($"{prefix}section-instructions:{id}", id))
            .ToList();

    private static IReadOnlyList<IElement> Fields(IRenderedComponent<Consults> page) =>
        page.FindAll("label.input-field");

    [Fact]
    public void LegacyPackage_RendersTheSingleDraftField()
    {
        // A package that declares no inputs is the v5/v6 shape: the page
        // synthesizes the frozen consult_draft slot.
        WithPinnedPackage(blocks: NineSections());

        var page = Render<Consults>();

        var field = Assert.Single(Fields(page));
        Assert.Contains("Consult draft", field.TextContent);
        Assert.DoesNotContain("(optional)", field.TextContent);
    }

    [Fact]
    public void DeclaredInputs_RenderOneFieldEachWithTheOptionalMarker()
    {
        WithPinnedPackage(
            blocks: NineSections("consult_note:"),
            inputs: new[]
            {
                new WorkflowPackageInputResponse("consult_draft", "Consult draft", true),
                new WorkflowPackageInputResponse("prior_notes", "Prior notes", false)
            });

        var page = Render<Consults>();

        var fields = Fields(page);
        Assert.Equal(2, fields.Count);
        Assert.Contains("Consult draft", fields[0].TextContent);
        Assert.DoesNotContain("(optional)", fields[0].TextContent);
        Assert.Contains("Prior notes", fields[1].TextContent);
        Assert.Contains("(optional)", fields[1].TextContent);
    }

    [Fact]
    public void Submit_IsGatedOnEveryRequiredInput()
    {
        WithPinnedPackage(
            blocks: NineSections(),
            inputs: new[]
            {
                new WorkflowPackageInputResponse("consult_draft", "Consult draft", true),
                new WorkflowPackageInputResponse("prior_notes", "Prior notes", false)
            });

        var page = Render<Consults>();
        var submit = page.FindAll("fluent-button").Last();
        Assert.True(submit.HasAttribute("disabled"));

        // Filling only the optional input leaves the gate closed.
        page.FindAll("fluent-text-area")[1].Change("Old notes.");
        Assert.True(page.FindAll("fluent-button").Last().HasAttribute("disabled"));

        page.FindAll("fluent-text-area")[0].Change("Chest pain, rule out ACS.");
        Assert.False(page.FindAll("fluent-button").Last().HasAttribute("disabled"));
    }

    [Fact]
    public void MultiDeliverablePackage_GroupsTheSectionRosterByDocument()
    {
        WithPinnedPackage(
            blocks: NineSections("consult_note:").Concat(NineSections("patient_letter:")).ToList(),
            inputs: new[] { new WorkflowPackageInputResponse("consult_draft", "Consult draft", true) },
            results: new[]
            {
                new WorkflowPackageResultResponse("consult_note", "Consultation note"),
                new WorkflowPackageResultResponse("patient_letter", "Patient letter")
            });

        var page = Render<Consults>();

        Assert.Contains("2 documents · 9 sections each", page.Find(".setup-context").TextContent);
        var groups = page.FindAll(".setup-sections__group-label");
        Assert.Equal(
            new[] { "Consultation note", "Patient letter" },
            groups.Select(group => group.TextContent.Trim()).ToArray());
    }

    private static IRenderedComponent<Microsoft.AspNetCore.Components.Forms.InputFile> FileInput(
        IRenderedComponent<Consults> page,
        int index) =>
        page.FindComponents<Microsoft.AspNetCore.Components.Forms.InputFile>()[index];

    private static string FieldText(IRenderedComponent<Consults> page, int index) =>
        page.FindAll("fluent-text-area")[index].GetAttribute("current-value")
            ?? page.FindAll("fluent-text-area")[index].GetAttribute("value")
            ?? string.Empty;

    /// <summary>
    /// The parser reads every attachment (#235), so the client only ever sees
    /// its answer. Stubbing that answer is what these drive.
    /// </summary>
    private void WithExtraction(string text, string extractor = "text/1", int? pageCount = null) =>
        DocumentService.ExtractAsync(Arg.Any<byte[]>(), Arg.Any<string>())
            .Returns(new DocumentExtractionOutcome(text, extractor, pageCount, null));

    private void WithRefusal(string error) =>
        DocumentService.ExtractAsync(Arg.Any<byte[]>(), Arg.Any<string>())
            .Returns(DocumentExtractionOutcome.Refused(error));

    private static IElement Field(IRenderedComponent<Consults> page, int index) =>
        page.FindAll("label.input-field")[index];

    [Fact]
    public void AttachingAFile_ReplacesThatSlotsTextAreaOnly()
    {
        // The per-slot behaviour v7 made possible: two declared inputs, and a
        // file aimed at the second leaves the first's text area alone.
        WithPinnedPackage(
            blocks: NineSections(),
            inputs: new[]
            {
                new WorkflowPackageInputResponse("consult_draft", "Consult draft", true),
                new WorkflowPackageInputResponse("prior_notes", "Prior notes", false)
            });
        WithExtraction("Old records.");

        var page = Render<Consults>();
        FileInput(page, 1).UploadFiles(InputFileContent.CreateFromText("Old records.", "records.txt"));

        Assert.Single(page.FindAll("fluent-text-area"));
        Assert.Empty(Field(page, 0).QuerySelectorAll(".input-field__chip"));
        Assert.Contains("records.txt", Field(page, 1).QuerySelector(".input-field__chip")!.TextContent);
    }

    [Fact]
    public void AttachingAFile_ShowsWhatTheServerReadFromIt()
    {
        // Not decoration: extraction is lossy on columns and tables, so the
        // read has to be visible while rejecting it is still cheap.
        WithPinnedPackage(blocks: NineSections());
        WithExtraction("Emily Lee is a 54 year old woman.", "pdfpig/0.1.15", pageCount: 3);

        var page = Render<Consults>();
        FileInput(page, 0).UploadFiles(InputFileContent.CreateFromText("%PDF-1.7", "referral.pdf"));

        Assert.Equal("Emily Lee is a 54 year old woman.", page.Find(".input-field__preview").TextContent);
        Assert.Contains("3 pages", page.Find(".input-field__chip").TextContent);
    }

    [Fact]
    public void RemovingTheFile_GivesBackWhatWasTyped()
    {
        WithPinnedPackage(blocks: NineSections());
        WithExtraction("From the file.");

        var page = Render<Consults>();
        page.Find("fluent-text-area").Change("Typed by hand.");
        FileInput(page, 0).UploadFiles(InputFileContent.CreateFromText("From the file.", "referral.md"));
        Assert.Empty(page.FindAll("fluent-text-area"));

        page.FindAll("fluent-button").First(button => button.TextContent.Contains("Remove")).Click();

        Assert.Equal("Typed by hand.", FieldText(page, 0));
        Assert.Empty(page.FindAll(".input-field__chip"));
    }

    [Fact]
    public void ARefusedFile_ShowsTheServersSentenceAndChangesNothing()
    {
        // The sentence is the server's (DocumentExtractionCopy), rendered
        // verbatim — one copy of the copy, shared with the email door.
        WithPinnedPackage(blocks: NineSections());
        WithRefusal("This PDF has no text layer, so it is a scan or a fax.");

        var page = Render<Consults>();
        page.Find("fluent-text-area").Change("Typed by hand.");
        FileInput(page, 0).UploadFiles(InputFileContent.CreateFromText("%PDF-1.7", "scan.pdf"));

        Assert.Equal(
            "This PDF has no text layer, so it is a scan or a fax.",
            page.Find(".input-field__file-error").TextContent);
        Assert.Equal("Typed by hand.", FieldText(page, 0));
        Assert.Empty(page.FindAll(".input-field__chip"));
    }

    [Fact]
    public void UploadingAnOversizeFile_IsRefusedBeforeAnyBytesAreSent()
    {
        WithPinnedPackage(blocks: NineSections());

        var page = Render<Consults>();
        FileInput(page, 0).UploadFiles(
            InputFileContent.CreateFromText(new string('x', (10 * 1024 * 1024) + 1), "big.pdf"));

        Assert.Contains("larger than 10 MB", page.Find(".input-field__file-error").TextContent);
        DocumentService.DidNotReceive().ExtractAsync(Arg.Any<byte[]>(), Arg.Any<string>());
    }

    [Fact]
    public void AttachingIntoARequiredSlot_OpensTheSubmitGate()
    {
        // The interaction most likely to regress: every gate used to read
        // field.Value, and a file-backed slot has none.
        WithPinnedPackage(blocks: NineSections());
        WithExtraction("Referral body.");

        var page = Render<Consults>();
        Assert.True(page.FindAll("fluent-button").Last().HasAttribute("disabled"));

        FileInput(page, 0).UploadFiles(InputFileContent.CreateFromText("Referral body.", "referral.txt"));

        Assert.False(page.FindAll("fluent-button").Last().HasAttribute("disabled"));
    }

    [Fact]
    public void Submitting_SendsTheFileItselfAndTypedSlotsAsText()
    {
        // The whole point of the model: the bytes travel, not the preview. The
        // server extracts them again, so a slot's origin is something it
        // observed rather than something this client asserted.
        WithPinnedPackage(
            blocks: NineSections(),
            inputs: new[]
            {
                new WorkflowPackageInputResponse("consult_draft", "Consult draft", true),
                new WorkflowPackageInputResponse("prior_notes", "Prior notes", false)
            });
        WithExtraction("Old records, as read.");

        IReadOnlyDictionary<string, string>? sentInputs = null;
        IReadOnlyDictionary<string, InputFilePayload>? sentFiles = null;
        AIService.StartConsultGenerationJobAsync(
                Arg.Do<IReadOnlyDictionary<string, string>>(value => sentInputs = value),
                Arg.Any<string?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Do<IReadOnlyDictionary<string, InputFilePayload>?>(value => sentFiles = value))
            .Returns(new ConsultGenerationJobStartResponse("job-1", "https://example/status"));

        var page = Render<Consults>();
        page.Find("fluent-text-area").Change("Typed referral.");
        FileInput(page, 1).UploadFiles(InputFileContent.CreateFromText("Old records.", "records.txt"));
        page.FindAll("fluent-button").Last().Click();

        Assert.NotNull(sentInputs);
        Assert.Equal(new[] { "consult_draft" }, sentInputs!.Keys.ToArray());
        Assert.Equal("Typed referral.", sentInputs["consult_draft"]);

        Assert.NotNull(sentFiles);
        Assert.Equal(new[] { "prior_notes" }, sentFiles!.Keys.ToArray());
        Assert.Equal("Old records.", System.Text.Encoding.UTF8.GetString(sentFiles["prior_notes"].Content));
    }

    [Fact]
    public void SingleDeliverablePackage_KeepsTheFlatSectionWording()
    {
        WithPinnedPackage(
            blocks: NineSections("consult:"),
            inputs: new[] { new WorkflowPackageInputResponse("consult_draft", "Consult draft", true) },
            results: new[] { new WorkflowPackageResultResponse("consult", "Consultation note") });

        var page = Render<Consults>();

        Assert.Contains("9 sections", page.Find(".setup-context").TextContent);
        Assert.DoesNotContain("documents", page.Find(".setup-context").TextContent);
        Assert.Empty(page.FindAll(".setup-sections__group-label"));
    }
}
