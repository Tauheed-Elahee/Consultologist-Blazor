using Bunit;
using Consultologist.Web.Pages;
using NSubstitute;

namespace Consultologist.Web.Tests;

/// <summary>
/// Pins the editor's existing behavior before the v7 authoring work (#218)
/// changes it. Templates.razor is the largest component in the app and had no
/// render coverage; these cover the surfaces that must survive untouched.
/// </summary>
public class TemplatesEditorTests : ClientRenderTestContext
{
    /// <summary>
    /// The editor opens on the first data file, so every test that wants the
    /// node cards or the deliverable selector navigates to the Graph pane
    /// first — the same click a user makes.
    /// </summary>
    private IRenderedComponent<Templates> RenderEditor(bool v7 = false)
    {
        WorkflowService.GetCurrentPackageContentAsync()
            .Returns(v7 ? EditorFixtures.V7() : EditorFixtures.V6());

        var page = Render<Templates>();

        page.FindAll("button.editor-nav__item")
            .First(button => button.TextContent.Trim() == "Graph")
            .Click();

        return page;
    }

    [Fact]
    public void NodeCards_RenderOnePerManifestNode()
    {
        var page = RenderEditor();

        var labels = page.FindAll(".template-section .node-summary__title, .template-section h3")
            .Select(element => element.TextContent)
            .ToList();

        Assert.Contains(labels, label => label.Contains("Drafting section"));
        Assert.Contains(labels, label => label.Contains("Assembling note"));
    }

    [Fact]
    public void DeliverableSelector_ListsAggregatorsAndMarksTheCurrentOne()
    {
        var page = RenderEditor();

        var select = page.Find(".result-selector select");
        var options = select.QuerySelectorAll("option").Select(o => o.GetAttribute("value")).ToArray();

        // v6: aggregators are the candidates; the fan node is not one.
        Assert.Equal(new[] { "node:assemble-note" }, options);
        Assert.Equal("node:assemble-note", select.GetAttribute("value"));
    }

    [Fact]
    public void BindingRows_OfferTheFrozenInputAndSiblingNodes()
    {
        var page = RenderEditor();

        // The fan node binds consult_draft; its source select must offer the
        // frozen input plus the item fields a forEach node can read.
        var options = page.FindAll(".binding-row__select option")
            .Select(option => option.GetAttribute("value"))
            .ToList();

        Assert.Contains("input:consult_draft", options);
        Assert.Contains("item:name", options);
    }

    [Fact]
    public void FreshlyLoaded_HasNoPendingEdits()
    {
        var page = RenderEditor();

        // The editor diffs against the manifest rather than counting
        // interactions, so re-selecting the current deliverable is not a change.
        page.Find(".result-selector select").Change("node:assemble-note");

        Assert.Empty(page.FindAll(".binding-row__pending"));
        Assert.All(
            page.FindAll("fluent-button").Where(b => b.TextContent.Contains("Publish") || b.TextContent.Contains("Discard")),
            button => Assert.True(button.HasAttribute("disabled")));
    }

    [Fact]
    public void V7Package_LoadsWithoutError()
    {
        // Before #218 this renders, but the deliverable selector is empty and
        // the candidates are wrong — the tests below the repair commits assert
        // the corrected behavior.
        var page = RenderEditor(v7: true);

        Assert.Contains("Assembling note", page.Markup);
    }
}
