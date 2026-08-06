using System.Text;

namespace Consultologist.Api.Email;

/// <summary>
/// The result of making text drawable: the text itself, the codepoints that
/// had no glyph and no safe stand-in, and — since #302 — the ones that did.
///
/// <paramref name="Folded"/> is the near-miss count. Substitutions used to
/// happen silently, so a run reporting no <paramref name="Unrenderable"/>
/// could not say whether the fold had saved it constantly or never been
/// needed. That difference is exactly what #287's decision turns on.
/// </summary>
internal sealed record PreparedText(
    string Text,
    IReadOnlyDictionary<int, int> Unrenderable,
    IReadOnlyDictionary<int, int> Folded);

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
///
/// Whatever survives both — no glyph and no same-mark stand-in — becomes
/// U+25A1 WHITE SQUARE (#287) rather than being kept. Keeping it meant the
/// font drew <c>.notdef</c> and the copy carried U+0000, which is the defect
/// #252 existed to end; U+25A1 is what <c>.notdef</c> already looks like here,
/// so the page is unchanged and only the copy buffer is fixed. That is a
/// lossy edit and the only one in this class: the codepoint is gone from the
/// document, which is why it is counted (<see cref="PreparedText.Unrenderable"/>)
/// and warned about at delivery.
/// </summary>
internal static class RenderableText
{
    /// <summary>
    /// What a character with no glyph and no stand-in becomes (#287): WHITE
    /// SQUARE, chosen because it is what <c>.notdef</c> already renders as, so
    /// substituting it changes the copy buffer without changing the page.
    /// </summary>
    private const char MissingGlyphMark = '□';

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
            return new PreparedText(text, EmptyCounts, EmptyCounts);
        }

        StringBuilder? rewritten = null;
        Dictionary<int, int>? unrenderable = null;
        Dictionary<int, int>? folded = null;

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

                // #302: counted for the same reason the residue is. A fold is
                // the case that went well, and its frequency is the only
                // evidence for how close the delivered document came to
                // needing the stand-in that does not exist.
                folded ??= new Dictionary<int, int>();
                folded[ch] = folded.GetValueOrDefault(ch) + 1;
                continue;
            }

            // No glyph and no safe stand-in (#287). Keeping the character was
            // the old answer and it was too generous: the font draws .notdef,
            // and the PDF's ToUnicode map then faithfully records that the
            // glyph means nothing — so the copy carries U+0000, and Outlook on
            // the web drops the character *after* it, turning "here" into
            // "ere" in a note pasted into a chart.
            //
            // U+25A1 was chosen because Liberation Sans's .notdef is already a
            // hollow rectangle (glyph 0, two contours), so the reader's signal
            // that something is missing is as strong as before and the page
            // barely moves — the two outlines differ in proportion and weight,
            // so "identical" would be too strong. What actually changes is the
            // copy buffer: a real glyph the font has, rather than a hole.
            //
            // The shape does not vary by reader: the font is embedded in the
            // PDF, so every viewer draws Liberation Sans's glyph rather than a
            // system fallback. The *copy* behaviour did vary, which is the
            // defect — PdfPig yields U+0000, Outlook on the web additionally
            // drops the following character.
            //
            // Guarded on coverage, so a font without U+25A1 keeps the original
            // rather than trading one missing glyph for another.
            rewritten.Append(coverage.Covers(MissingGlyphMark) ? MissingGlyphMark : ch);

            // The ORIGINAL codepoint is counted, never the mark. The count
            // exists to say which characters are arriving; recording U+25A1
            // would answer a question nobody asked.
            unrenderable ??= new Dictionary<int, int>();
            unrenderable[ch] = unrenderable.GetValueOrDefault(ch) + 1;
        }

        return new PreparedText(
            rewritten?.ToString() ?? text,
            unrenderable ?? EmptyCounts,
            folded ?? EmptyCounts);
    }

    private static readonly IReadOnlyDictionary<int, int> EmptyCounts = new Dictionary<int, int>();

    /// <summary>
    /// Codepoints only, never the characters in context — a codepoint names
    /// no patient, a surrounding phrase might. Used for both counts (#302):
    /// the rule is the same whichever way the character went.
    /// </summary>
    internal static string Describe(IReadOnlyDictionary<int, int> counts) =>
        string.Join(", ", counts.OrderBy(pair => pair.Key).Select(pair => $"U+{pair.Key:X4}x{pair.Value}"));
}
