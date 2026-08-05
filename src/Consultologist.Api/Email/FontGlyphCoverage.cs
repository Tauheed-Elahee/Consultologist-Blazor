using System.Buffers.Binary;

namespace Consultologist.Api.Email;

/// <summary>
/// Whether the embedded font's coverage could be read, and if not, why (#302).
///
/// Every value but <see cref="Read"/> means the fold is inert: nothing is
/// substituted and nothing is counted. That is the correct failure direction —
/// see <see cref="FontGlyphCoverage"/> — but it is indistinguishable from a
/// clean run unless the reason is reported, which is what this exists for.
/// </summary>
internal enum FontCoverageStatus
{
    /// <summary>The cmap parsed and mapped at least one codepoint.</summary>
    Read,

    /// <summary>The resolver returned no font bytes at all.</summary>
    FontMissing,

    /// <summary>No <c>cmap</c> table in the font's table directory.</summary>
    NoCmapTable,

    /// <summary>A <c>cmap</c>, but no format-4 subtable in it.</summary>
    NoFormat4Subtable,

    /// <summary>Parsed, but the subtable mapped no codepoint to a real glyph.</summary>
    NoCodepointsMapped,

    /// <summary>Malformed bytes, or a shape this reader does not handle.</summary>
    ParseFailed
}

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
///
/// **And says so** (#302). Failing open silently made the fold indetectably
/// inert: with coverage unknown nothing is folded and nothing is counted, so
/// production reported zero undrawable characters — the same answer a clean
/// run gives. <see cref="Status"/> is what separates those two.
/// </summary>
internal sealed class FontGlyphCoverage
{
    private readonly HashSet<int>? _covered;

    private FontGlyphCoverage(HashSet<int>? covered, FontCoverageStatus status)
    {
        _covered = covered;
        Status = status;
    }

    /// <summary>Why coverage is or is not known — the diagnostic #302 added.</summary>
    internal FontCoverageStatus Status { get; }

    /// <summary>True when coverage could not be read and every character is assumed drawable.</summary>
    internal bool IsUnknown => Status != FontCoverageStatus.Read;

    internal bool Covers(int codepoint) => _covered?.Contains(codepoint) ?? true;

    /// <summary>
    /// No font bytes to read at all. Its own status because the cause is the
    /// caller's — a resolver that returned nothing — rather than anything
    /// about a font's contents.
    /// </summary>
    internal static FontGlyphCoverage Missing() =>
        new(null, FontCoverageStatus.FontMissing);

    internal static FontGlyphCoverage Read(byte[] font)
    {
        try
        {
            var (covered, status) = ReadFormat4(font);
            return new FontGlyphCoverage(covered, status);
        }
        catch (Exception)
        {
            // Malformed, unusual, or a format this does not read. Assume full
            // coverage rather than risk substituting text we did not need to.
            return new FontGlyphCoverage(null, FontCoverageStatus.ParseFailed);
        }
    }

    private static (HashSet<int>? Covered, FontCoverageStatus Status) ReadFormat4(byte[] font)
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
            return (null, FontCoverageStatus.NoCmapTable);
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
            return (null, FontCoverageStatus.NoFormat4Subtable);
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

        // Zero mapped codepoints is a parse that technically succeeded and told
        // us nothing. Treated as unknown rather than as "this font draws
        // nothing", which would fold every character in the document.
        return covered.Count > 0
            ? (covered, FontCoverageStatus.Read)
            : (null, FontCoverageStatus.NoCodepointsMapped);
    }

    private static ushort U16(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));

    private static uint U32(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
}
