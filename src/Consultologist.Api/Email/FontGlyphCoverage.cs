using System.Buffers.Binary;

namespace Consultologist.Api.Email;

/// <summary>
/// Which characters the embedded font can actually draw (#252).
///
/// A font with no glyph for a character does not fail — it draws
/// <c>.notdef</c>, and the PDF's ToUnicode map then faithfully records that
/// the glyph means nothing. Readers copy that out as a control character,
/// which is how <c>hormone‑blocking</c> reached a clinician as
/// <c>hormone␀blocking</c>. The font is the authority on what it can draw,
/// so this reads it rather than guessing.
///
/// Reads the OpenType <c>cmap</c> table, format 4 (the BMP subtable every
/// TrueType font carries). A glyph index of 0 is <c>.notdef</c> and counts
/// as absent — a range can claim codepoints it maps to nothing.
///
/// **Fails open by design.** If the table cannot be parsed, every character
/// is reported as covered, so nothing is substituted and the text is
/// delivered exactly as written. Guessing wrong in the other direction would
/// silently edit clinical prose, which is far worse than the defect this
/// exists to fix.
/// </summary>
internal sealed class FontGlyphCoverage
{
    private readonly HashSet<int>? _covered;

    private FontGlyphCoverage(HashSet<int>? covered) => _covered = covered;

    /// <summary>True when the parse failed and every character is assumed drawable.</summary>
    internal bool IsUnknown => _covered == null;

    internal bool Covers(int codepoint) => _covered?.Contains(codepoint) ?? true;

    internal static FontGlyphCoverage Read(byte[] font)
    {
        try
        {
            return new FontGlyphCoverage(ReadFormat4(font));
        }
        catch (Exception)
        {
            // Malformed, unusual, or a format this does not read. Assume full
            // coverage rather than risk substituting text we did not need to.
            return new FontGlyphCoverage(null);
        }
    }

    private static HashSet<int>? ReadFormat4(byte[] font)
    {
        var tableCount = U16(font, 4);
        var cmap = -1;

        for (var i = 0; i < tableCount; i++)
        {
            var record = 12 + (i * 16);
            if (font[record] == 'c' && font[record + 1] == 'm' && font[record + 2] == 'a' && font[record + 3] == 'p')
            {
                cmap = (int)U32(font, record + 8);
                break;
            }
        }

        if (cmap < 0)
        {
            return null;
        }

        var subtable = -1;
        var encodingCount = U16(font, cmap + 2);

        for (var i = 0; i < encodingCount; i++)
        {
            var offset = cmap + (int)U32(font, cmap + 4 + (i * 8) + 4);
            if (U16(font, offset) == 4)
            {
                subtable = offset;
            }
        }

        if (subtable < 0)
        {
            return null;
        }

        var segCount = U16(font, subtable + 6) / 2;
        var endCodes = subtable + 14;
        var startCodes = endCodes + (segCount * 2) + 2;
        var idDeltas = startCodes + (segCount * 2);
        var idRangeOffsets = idDeltas + (segCount * 2);

        var covered = new HashSet<int>();

        for (var segment = 0; segment < segCount; segment++)
        {
            int start = U16(font, startCodes + (segment * 2));
            int end = U16(font, endCodes + (segment * 2));
            int delta = U16(font, idDeltas + (segment * 2));
            int rangeOffset = U16(font, idRangeOffsets + (segment * 2));

            if (start > end || end == 0xFFFF && start == 0xFFFF)
            {
                continue;
            }

            for (var code = start; code <= end; code++)
            {
                int glyph;

                if (rangeOffset == 0)
                {
                    glyph = (code + delta) & 0xFFFF;
                }
                else
                {
                    // The spec's pointer arithmetic: the offset is measured
                    // from the idRangeOffset slot itself, not the array start.
                    var at = idRangeOffsets + (segment * 2) + rangeOffset + ((code - start) * 2);
                    if (at + 1 >= font.Length)
                    {
                        continue;
                    }

                    glyph = U16(font, at);
                    if (glyph != 0)
                    {
                        glyph = (glyph + delta) & 0xFFFF;
                    }
                }

                if (glyph != 0)
                {
                    covered.Add(code);
                }
            }
        }

        return covered.Count > 0 ? covered : null;
    }

    private static ushort U16(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));

    private static uint U32(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
}
