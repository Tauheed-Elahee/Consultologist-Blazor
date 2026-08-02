using Consultologist.Api.Email;

namespace Consultologist.Api.Tests;

/// <summary>
/// #252: the folding rule, against a coverage set stated by the test rather
/// than read from a font — so these assert the rule, not the font.
/// </summary>
public class RenderableTextTests
{
    /// <summary>Everything ASCII, plus whatever the test names.</summary>
    private static FontGlyphCoverage CoveringAsciiAnd(params int[] extra)
    {
        // Built from a real cmap so the type under test is exercised as it is
        // in production; a stub would test a different object.
        return FakeFont.WithCoverage(Enumerable.Range(0x20, 0x5F).Concat(extra).ToArray());
    }

    [Fact]
    public void ACharacterTheFontCanDraw_IsUntouched()
    {
        var prepared = RenderableText.Prepare("plain prose", CoveringAsciiAnd());

        Assert.Equal("plain prose", prepared.Text);
        Assert.Empty(prepared.Unrenderable);
    }

    [Fact]
    public void ANonBreakingHyphen_BecomesAHyphenWhenTheFontLacksIt()
    {
        var prepared = RenderableText.Prepare("hormone‑blocking", CoveringAsciiAnd(0x2010));

        Assert.Equal("hormone‐blocking", prepared.Text);
        Assert.Empty(prepared.Unrenderable);
    }

    [Fact]
    public void ANonBreakingHyphen_IsLeftAloneWhenTheFontHasIt()
    {
        // The substitution is conditional on the gap, so a wider font retires
        // the entry rather than changing behaviour.
        var prepared = RenderableText.Prepare("hormone‑blocking", CoveringAsciiAnd(0x2010, 0x2011));

        Assert.Equal("hormone‑blocking", prepared.Text);
    }

    [Fact]
    public void AZeroWidthCharacter_IsRemovedRatherThanSubstituted()
    {
        var prepared = RenderableText.Prepare("word​joiner", CoveringAsciiAnd());

        Assert.Equal("wordjoiner", prepared.Text);
        Assert.Empty(prepared.Unrenderable);
    }

    [Fact]
    public void AnUndrawableCharacterWithNoStandIn_IsKeptAndCounted()
    {
        // Kept, because a missing-glyph box is visible to whoever reads the
        // page; counted, because that is what stops it being silent to us.
        var prepared = RenderableText.Prepare("a 一 b 一", CoveringAsciiAnd());

        Assert.Equal("a 一 b 一", prepared.Text);
        Assert.Equal(2, prepared.Unrenderable[0x4E00]);
    }

    [Fact]
    public void UnknownCoverage_ChangesNothingAtAll()
    {
        var prepared = RenderableText.Prepare("hormone‑blocking", FontGlyphCoverage.Read([0, 1, 2]));

        Assert.Equal("hormone‑blocking", prepared.Text);
        Assert.Empty(prepared.Unrenderable);
    }

    [Fact]
    public void Describe_NamesCodepointsAndCountsOnly()
    {
        // Never the surrounding text: a codepoint names no patient, a phrase
        // might.
        var described = RenderableText.Describe(new Dictionary<int, int> { [0x4E00] = 2, [0x2620] = 1 });

        Assert.Equal("U+2620x1, U+4E00x2", described);
    }

    [Fact]
    public void SubstitutionIsSkippedWhenTheStandInIsAlsoMissing()
    {
        // No U+2010 either, so there is nowhere safe to put U+2011.
        var prepared = RenderableText.Prepare("a‑b", CoveringAsciiAnd());

        Assert.Equal("a‑b", prepared.Text);
        Assert.Equal(1, prepared.Unrenderable[0x2011]);
    }
}

/// <summary>
/// A minimal TrueType file carrying only a cmap, so coverage can be stated by
/// a test without shipping fixture fonts.
/// </summary>
internal static class FakeFont
{
    internal static FontGlyphCoverage WithCoverage(params int[] codepoints)
    {
        var codes = codepoints.Where(c => c is > 0 and < 0xFFFF).Distinct().OrderBy(c => c).ToArray();

        // One segment per contiguous run, plus the mandatory 0xFFFF terminator.
        var segments = new List<(int Start, int End)>();
        foreach (var code in codes)
        {
            if (segments.Count > 0 && segments[^1].End == code - 1)
            {
                segments[^1] = (segments[^1].Start, code);
            }
            else
            {
                segments.Add((code, code));
            }
        }

        segments.Add((0xFFFF, 0xFFFF));
        var segCount = segments.Count;

        var sub = new List<byte>();
        void U16(int v) { sub.Add((byte)(v >> 8)); sub.Add((byte)v); }

        U16(4);                       // format
        U16(0);                       // length (unused by the reader)
        U16(0);                       // language
        U16(segCount * 2);            // segCountX2
        U16(0); U16(0); U16(0);       // searchRange, entrySelector, rangeShift
        foreach (var s in segments) U16(s.End);
        U16(0);                       // reservedPad
        foreach (var s in segments) U16(s.Start);
        // idDelta maps every covered code to a non-zero glyph; the 0xFFFF
        // terminator must map to 0 so it is not reported as drawable.
        foreach (var s in segments) U16(s.Start == 0xFFFF ? (0x10000 - 0xFFFF) & 0xFFFF : 1);
        foreach (var _ in segments) U16(0);   // idRangeOffset

        var subtable = sub.ToArray();
        var cmap = new List<byte>();
        void C16(int v) { cmap.Add((byte)(v >> 8)); cmap.Add((byte)v); }
        void C32(long v) { cmap.Add((byte)(v >> 24)); cmap.Add((byte)(v >> 16)); cmap.Add((byte)(v >> 8)); cmap.Add((byte)v); }

        C16(0); C16(1);               // version, numTables
        C16(3); C16(1); C32(12);      // platform 3, encoding 1, offset 12
        cmap.AddRange(subtable);

        var font = new List<byte>();
        void F16(int v) { font.Add((byte)(v >> 8)); font.Add((byte)v); }
        void F32(long v) { font.Add((byte)(v >> 24)); font.Add((byte)(v >> 16)); font.Add((byte)(v >> 8)); font.Add((byte)v); }

        F32(0x00010000);              // sfnt version
        F16(1);                       // numTables
        F16(0); F16(0); F16(0);       // searchRange, entrySelector, rangeShift
        font.AddRange("cmap"u8.ToArray());
        F32(0);                       // checksum
        F32(28);                      // offset — 12 header + 16 record
        F32(cmap.Count);              // length
        font.AddRange(cmap);

        return FontGlyphCoverage.Read(font.ToArray());
    }
}
