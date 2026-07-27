using AngleSharp.Dom;
using Bunit;
using Consultologist.Web.Pages;
using Consultologist.Web.Services.Workflow;

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

    [Fact]
    public void UploadingText_FillsOnlyTheTargetedSlot()
    {
        // The per-slot behaviour v7 made possible: two declared inputs, and an
        // upload aimed at the second leaves the first alone.
        WithPinnedPackage(
            blocks: NineSections(),
            inputs: new[]
            {
                new WorkflowPackageInputResponse("consult_draft", "Consult draft", true),
                new WorkflowPackageInputResponse("prior_notes", "Prior notes", false)
            });

        var page = Render<Consults>();
        FileInput(page, 1).UploadFiles(InputFileContent.CreateFromText("Old records.", "records.txt"));

        Assert.Equal(string.Empty, FieldText(page, 0));
        Assert.Equal("Old records.", FieldText(page, 1));
        Assert.Contains("loaded records.txt", page.Markup);
    }

    [Fact]
    public void UploadingOverExistingText_ReplacesIt()
    {
        WithPinnedPackage(blocks: NineSections());

        var page = Render<Consults>();
        page.Find("fluent-text-area").Change("Typed by hand.");
        FileInput(page, 0).UploadFiles(InputFileContent.CreateFromText("From the file.", "referral.md"));

        Assert.Equal("From the file.", FieldText(page, 0));
    }

    [Fact]
    public void UploadingAnUnsupportedType_IsRefusedInlineAndChangesNothing()
    {
        WithPinnedPackage(blocks: NineSections());

        var page = Render<Consults>();
        page.Find("fluent-text-area").Change("Typed by hand.");
        FileInput(page, 0).UploadFiles(InputFileContent.CreateFromText("%PDF-1.7", "referral.pdf"));

        // The wording names a current limit, not a permanent one — PDF is the
        // issue's phase 2.
        Assert.Contains("can be uploaded yet", page.Find(".input-field__file-error").TextContent);
        Assert.Equal("Typed by hand.", FieldText(page, 0));
    }

    [Fact]
    public void UploadingAnOversizeFile_IsRefusedInline()
    {
        WithPinnedPackage(blocks: NineSections());

        var page = Render<Consults>();
        FileInput(page, 0).UploadFiles(
            InputFileContent.CreateFromText(new string('x', (256 * 1024) + 1), "big.txt"));

        Assert.Contains("larger than 256 KB", page.Find(".input-field__file-error").TextContent);
        Assert.Equal(string.Empty, FieldText(page, 0));
    }

    [Fact]
    public void UploadingIntoARequiredSlot_OpensTheSubmitGate()
    {
        // The interaction most likely to regress: the gate reads field.Value,
        // which the upload path writes rather than the bind.
        WithPinnedPackage(blocks: NineSections());

        var page = Render<Consults>();
        Assert.True(page.FindAll("fluent-button").Last().HasAttribute("disabled"));

        FileInput(page, 0).UploadFiles(InputFileContent.CreateFromText("Referral body.", "referral.txt"));

        Assert.False(page.FindAll("fluent-button").Last().HasAttribute("disabled"));
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
