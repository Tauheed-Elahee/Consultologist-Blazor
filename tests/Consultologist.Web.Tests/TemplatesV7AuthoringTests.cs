using AngleSharp.Dom;
using Bunit;
using Consultologist.Web.Pages;
using NSubstitute;

namespace Consultologist.Web.Tests;

/// <summary>
/// The v7 authoring surfaces (#218): declared inputs, the document list, and
/// the repairs that made v7 editable at all.
/// </summary>
public class TemplatesV7AuthoringTests : ClientRenderTestContext
{
    private IRenderedComponent<Templates> RenderEditor(bool v7 = true)
    {
        WorkflowService.GetCurrentPackageContentAsync()
            .Returns(v7 ? EditorFixtures.V7() : EditorFixtures.V6());

        return Render<Templates>();
    }

    private static void Navigate(IRenderedComponent<Templates> page, string label) =>
        page.FindAll("button.editor-nav__item")
            .First(button => button.TextContent.Replace("\u25CF", string.Empty).Trim() == label)
            .Click();

    private static IReadOnlyList<IElement> Rows(IRenderedComponent<Templates> page) =>
        page.FindAll("li.declared-row");

    [Fact]
    public void LegacyPackage_HasNoDeclaredSections()
    {
        var page = RenderEditor(v7: false);

        var navLabels = page.FindAll("button.editor-nav__item").Select(b => b.TextContent.Trim()).ToList();
        Assert.DoesNotContain("Inputs", navLabels);
        Assert.DoesNotContain("Documents", navLabels);
    }

    [Fact]
    public void InputsPane_RendersOneRowPerDeclaredSlot()
    {
        var page = RenderEditor();
        Navigate(page, "Inputs");

        var rows = Rows(page);
        Assert.Equal(2, rows.Count);
        Assert.Equal("consult_draft", rows[0].QuerySelector("input.declared-row__id")!.GetAttribute("value"));
        Assert.Equal("prior_notes", rows[1].QuerySelector("input.declared-row__id")!.GetAttribute("value"));
        // The optional slot's checkbox is unchecked.
        Assert.False(rows[1].QuerySelector("input[type=checkbox]")!.HasAttribute("checked"));
    }

    [Fact]
    public void RenamingAnInput_CascadesIntoBindingsThatUsedIt()
    {
        var page = RenderEditor();
        Navigate(page, "Inputs");

        Rows(page)[0].QuerySelector("input.declared-row__id")!.Change("referral");

        // The fan node bound input:consult_draft; after the rename its binding
        // must point at the new id, or publishing would fail on an undeclared
        // input the author never touched.
        Navigate(page, "Graph");
        var sources = page.FindAll(".binding-row__select")
            .Select(select => select.GetAttribute("value"))
            .ToList();

        Assert.Contains("input:referral", sources);
        Assert.DoesNotContain("input:consult_draft", sources);
    }

    [Fact]
    public void DuplicateInputId_IsRefusedAtTheDesk()
    {
        var page = RenderEditor();
        Navigate(page, "Inputs");

        page.Find(".add-variable__form input.node-field__input").Change("consult_draft");
        page.Find(".add-variable__form button").Click();

        Assert.Contains("Duplicate input id 'consult_draft'", page.Markup);
        Assert.Equal(2, Rows(page).Count);
    }

    [Fact]
    public void MalformedInputId_IsRefusedWithTheServersWording()
    {
        var page = RenderEditor();
        Navigate(page, "Inputs");

        page.Find(".add-variable__form input.node-field__input").Change("Prior-Notes");
        page.Find(".add-variable__form button").Click();

        Assert.Contains("must be snake_case", page.Markup);
        Assert.Equal(2, Rows(page).Count);
    }

    [Fact]
    public void DocumentsPane_RendersTheDeclaredResultsWithAggregatorCandidates()
    {
        var page = RenderEditor();
        Navigate(page, "Documents");

        var row = Assert.Single(Rows(page));
        Assert.Equal("consult_note", row.QuerySelector("input.declared-row__id")!.GetAttribute("value"));

        // Candidates are aggregators only — a forEach node is not a valid v7
        // deliverable, which is the bug this pane replaced.
        var options = row.QuerySelectorAll("option").Select(o => o.GetAttribute("value")).ToArray();
        Assert.Equal(new[] { "node:assemble-note" }, options);
    }

    [Fact]
    public void AddingASecondDocument_NeedsAFreeAggregator()
    {
        var page = RenderEditor();
        Navigate(page, "Documents");

        page.Find(".add-variable__form input.node-field__input").Change("patient_letter");
        page.Find(".add-variable__form button").Click();

        // The fixture has one aggregator and it is already spoken for.
        Assert.Contains("already owns a document", page.Markup);
        Assert.Single(Rows(page));
    }

    [Fact]
    public void DeclaringDocuments_MarksThePackageDirty()
    {
        var page = RenderEditor();
        Navigate(page, "Documents");

        Rows(page)[0].QuerySelector("input.node-field__input:not(.declared-row__id)")!.Change("Consult letter");

        Assert.Contains("documents", page.Markup);
        Assert.False(
            page.FindAll("fluent-button").First(b => b.TextContent.Contains("Publish")).HasAttribute("disabled"),
            "a pending document edit should enable Publish");
    }
}
