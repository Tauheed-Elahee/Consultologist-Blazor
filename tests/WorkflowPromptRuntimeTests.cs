using Consultologist.Api.Workflow;

namespace Consultologist.Api.Tests;

public class PromptTemplateRendererTests
{
    private static WorkflowPromptTemplate Template(string text, string? prelude = null) =>
        new("extract-patient-concepts", text, new[] { "consult_draft" }, prelude);

    [Fact]
    public void Render_InterpolatesVariables()
    {
        var result = PromptTemplateRenderer.Render(
            Template("Draft:\n{{ consult_draft }}"),
            new Dictionary<string, string> { ["consult_draft"] = "Patient draft text." });

        Assert.Equal("Draft:\nPatient draft text.", result);
    }

    [Fact]
    public void Render_PrependsPreludeWithBlankLine()
    {
        var result = PromptTemplateRenderer.Render(
            Template("{{ consult_draft }}", prelude: "Guidance text.\n"),
            new Dictionary<string, string> { ["consult_draft"] = "x" });

        Assert.Equal("Guidance text.\n\nx", result);
    }

    [Fact]
    public void Render_ThrowsWhenSuppliedVariablesDoNotMatchDeclared()
    {
        var extra = new Dictionary<string, string> { ["consult_draft"] = "x", ["section_name"] = "HPI" };
        var missing = new Dictionary<string, string>();

        Assert.Throws<InvalidOperationException>(() => PromptTemplateRenderer.Render(Template("{{ consult_draft }}"), extra));
        Assert.Throws<InvalidOperationException>(() => PromptTemplateRenderer.Render(Template("{{ consult_draft }}"), missing));
    }

    [Fact]
    public void Render_ThrowsOnUndeclaredVariableAccess_StrictMode()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PromptTemplateRenderer.Render(
            Template("{{ consult_draft }} {{ not_declared }}"),
            new Dictionary<string, string> { ["consult_draft"] = "x" }));

        Assert.Contains("failed to render", ex.Message);
    }
}
