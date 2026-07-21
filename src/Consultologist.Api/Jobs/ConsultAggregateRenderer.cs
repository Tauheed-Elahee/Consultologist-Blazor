namespace Consultologist.Api.Jobs;

/// <summary>
/// The aggregator's normative rendering (package-format-v6-design.md § 3):
/// sources in declared order joined by blank lines; a forEach source renders as
/// labeled blocks ("## {item name}", blank line, instance output) in collection
/// index order; a scalar source renders verbatim. No prologue, no epilogue, no
/// trailing newline — the bytes feed hashes and downstream prompt inputs, so the
/// spec pins them exactly (see the pinning tests).
/// </summary>
internal static class ConsultAggregateRenderer
{
    public abstract record Part;

    /// <summary>A scalar source's output (prompt node or upstream aggregator).</summary>
    public sealed record ScalarPart(string Text) : Part;

    /// <summary>A forEach source's instances, already in collection index order.</summary>
    public sealed record ForEachPart(IReadOnlyList<(string Name, string Text)> Blocks) : Part;

    public static string Render(IReadOnlyList<Part> parts)
    {
        return string.Join("\n\n", parts.Select(part => part switch
        {
            ScalarPart scalar => scalar.Text,
            ForEachPart forEach => string.Join(
                "\n\n",
                forEach.Blocks.Select(block => $"## {block.Name}\n\n{block.Text}")),
            _ => throw new InvalidOperationException($"Unknown aggregate part '{part.GetType().Name}'.")
        }));
    }
}
