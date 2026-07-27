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
