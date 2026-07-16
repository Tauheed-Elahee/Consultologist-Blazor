using System.Net;
using System.Text;

namespace Consultologist.Web.Shared.WorkflowEditor;

/// <summary>
/// The lightweight markdown preview the Templates page has always used
/// (headings, bullets, paragraphs; everything HTML-encoded) — extracted so the
/// editor's prompt and standards cards share one renderer.
/// </summary>
public static class MarkdownPreview
{
    public static string Render(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return "<p class=\"muted-text\">No content.</p>";
        }

        var builder = new StringBuilder();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var inList = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            if (string.IsNullOrEmpty(line))
            {
                if (inList)
                {
                    builder.AppendLine("</ul>");
                    inList = false;
                }

                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                if (!inList)
                {
                    builder.AppendLine("<ul>");
                    inList = true;
                }

                builder.Append("<li>");
                builder.Append(WebUtility.HtmlEncode(line[2..]));
                builder.AppendLine("</li>");
                continue;
            }

            if (inList)
            {
                builder.AppendLine("</ul>");
                inList = false;
            }

            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                builder.Append("<h4>");
                builder.Append(WebUtility.HtmlEncode(line[4..]));
                builder.AppendLine("</h4>");
            }
            else
            {
                builder.Append("<p>");
                builder.Append(WebUtility.HtmlEncode(line));
                builder.AppendLine("</p>");
            }
        }

        if (inList)
        {
            builder.AppendLine("</ul>");
        }

        return builder.ToString();
    }
}
