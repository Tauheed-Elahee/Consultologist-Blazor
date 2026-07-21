using Markdig;

namespace Consultologist.Web.Shared.WorkflowEditor;

/// <summary>
/// The shared markdown renderer behind every preview surface (the Templates
/// page, the editor's prompt and standards cards, and the v6 assembled note).
/// Raw HTML in the source is escaped, never injected — the output feeds a
/// MarkupString.
/// </summary>
public static class MarkdownPreview
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        // Single newlines break lines, matching how the standards and prompt
        // sources are written line-per-line.
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    public static string Render(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return "<p class=\"muted-text\">No content.</p>";
        }

        return Markdown.ToHtml(markdown, Pipeline);
    }
}
