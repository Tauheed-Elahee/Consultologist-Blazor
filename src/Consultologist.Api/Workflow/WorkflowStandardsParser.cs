using System.Text;

namespace Consultologist.Api.Workflow;

/// <summary>One section of a standards document: the pre-v5 bindable unit.</summary>
public sealed record WorkflowStandardsSection(string Id, string Name, string Content);

/// <summary>
/// The "## id: name" standards-markdown split, server-side: byte-parity port of the
/// frontend parser it replaces (Consults/Templates ParseStandards). Serves
/// specVersion ≤4 packages; v5 packages declare sections as a data collection and
/// never pass through here (docs/customizable-workflow/package-format-v5.md).
/// </summary>
public static class WorkflowStandardsParser
{
    public static IReadOnlyList<WorkflowStandardsSection> Parse(string markdown)
    {
        var sections = new List<WorkflowStandardsSection>();
        (string Id, string Name)? current = null;
        var content = new StringBuilder();

        void Flush()
        {
            if (current is { } heading)
            {
                sections.Add(new WorkflowStandardsSection(heading.Id, heading.Name, content.ToString().Trim()));
                content.Clear();
            }
        }

        foreach (var line in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();

                var heading = line[3..].Trim();
                var separatorIndex = heading.IndexOf(':');
                var id = separatorIndex >= 0 ? heading[..separatorIndex].Trim() : CreateId(heading);
                var name = separatorIndex >= 0 ? heading[(separatorIndex + 1)..].Trim() : heading;
                current = (id, name);
                continue;
            }

            if (current != null)
            {
                content.AppendLine(line);
            }
        }

        Flush();
        return sections;
    }

    private static string CreateId(string value)
        => value.Trim().ToLowerInvariant().Replace(' ', '_');
}
