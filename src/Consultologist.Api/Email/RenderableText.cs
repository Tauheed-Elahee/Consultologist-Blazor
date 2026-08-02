using System.Text;

namespace Consultologist.Api.Email;

/// <summary>
/// The result of making text drawable: the text itself, plus the codepoints
/// that had no glyph and no safe stand-in.
/// </summary>
internal sealed record PreparedText(string Text, IReadOnlyDictionary<int, int> Unrenderable);

/// <summary>
/// Folds characters the embedded font cannot draw onto ones it can (#252).
///
/// The bar for a substitution is deliberately high: the replacement must be
/// **visually and semantically the same character**, differing only in
/// typographic behaviour. U+2011 NON-BREAKING HYPHEN and U+2010 HYPHEN are
/// the same mark and differ only in whether a line may break there — in a PDF
/// with fixed line breaks that difference is already spent. Nothing here
/// changes what a clinician reads.
///
/// This is the same judgement <see cref="CanonicalText"/> makes for extracted
/// text and the same one it refuses to extend: line endings, yes; rejoining
/// hard-wrapped lines, no. De-hyphenating, transliterating accents, or
/// "simplifying" a µ would all be corrections to clinical prose and are not
/// done here.
///
/// A substitution only applies when the font genuinely lacks the original, so
/// adopting a wider font silently retires each entry rather than changing
/// behaviour.
/// </summary>
internal static class RenderableText
{
    /// <summary>
    /// Same mark, different typographic behaviour. Every entry is a pair a
    /// reader would not distinguish in print.
    /// </summary>
    private static readonly Dictionary<int, int> SameMark = new()
    {
        [0x2011] = 0x2010, // NON-BREAKING HYPHEN  → HYPHEN
        [0x2012] = 0x2013, // FIGURE DASH          → EN DASH
        [0x2007] = 0x0020, // FIGURE SPACE         → SPACE
        [0x2008] = 0x0020, // PUNCTUATION SPACE    → SPACE
        [0x2009] = 0x0020, // THIN SPACE           → SPACE
        [0x202F] = 0x0020, // NARROW NO-BREAK SPACE→ SPACE
        [0x2060] = -1,     // WORD JOINER          → removed (zero width)
        [0x200B] = -1,     // ZERO WIDTH SPACE     → removed
        [0xFEFF] = -1      // ZERO WIDTH NBSP      → removed
    };

    internal static PreparedText Prepare(string text, FontGlyphCoverage coverage)
    {
        if (coverage.IsUnknown || text.Length == 0)
        {
            return new PreparedText(text, EmptyCounts);
        }

        StringBuilder? rewritten = null;
        Dictionary<int, int>? unrenderable = null;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            // Control characters and surrogate halves are the caller's
            // business, not the font's — pass them through untouched.
            if (char.IsControl(ch) || char.IsSurrogate(ch) || coverage.Covers(ch))
            {
                rewritten?.Append(ch);
                continue;
            }

            rewritten ??= new StringBuilder(text.Length).Append(text, 0, i);

            if (SameMark.TryGetValue(ch, out var replacement)
                && (replacement < 0 || coverage.Covers(replacement)))
            {
                if (replacement >= 0)
                {
                    rewritten.Append((char)replacement);
                }

                continue;
            }

            // No glyph and no safe stand-in. Keep the character: a missing-
            // glyph box is at least visible to whoever reads the page, where
            // dropping it would be the silent loss this issue is about. The
            // count is what makes it visible to us.
            rewritten.Append(ch);
            unrenderable ??= new Dictionary<int, int>();
            unrenderable[ch] = unrenderable.GetValueOrDefault(ch) + 1;
        }

        return new PreparedText(
            rewritten?.ToString() ?? text,
            unrenderable ?? EmptyCounts);
    }

    private static readonly IReadOnlyDictionary<int, int> EmptyCounts = new Dictionary<int, int>();

    /// <summary>
    /// Codepoints only, never the characters in context — a codepoint names
    /// no patient, a surrounding phrase might.
    /// </summary>
    internal static string Describe(IReadOnlyDictionary<int, int> unrenderable) =>
        string.Join(", ", unrenderable.OrderBy(pair => pair.Key).Select(pair => $"U+{pair.Key:X4}x{pair.Value}"));
}
