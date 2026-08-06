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
    public void AnUndrawableCharacterWithNoStandIn_BecomesAWhiteSquareAndIsCounted()
    {
        // #287: keeping it meant the font drew .notdef and the copy carried
        // U+0000. U+25A1 is what .notdef already looks like, so the page is
        // unchanged and only the copy buffer is fixed.
        var prepared = RenderableText.Prepare("a 一 b 一", CoveringAsciiAnd(0x25A1));

        Assert.Equal("a □ b □", prepared.Text);
        // The ORIGINAL codepoint, never the mark — the count says which
        // characters are arriving, and U+25A1 would answer nothing.
        Assert.Equal(2, prepared.Unrenderable[0x4E00]);
        Assert.DoesNotContain(0x25A1, prepared.Unrenderable.Keys);
    }

    [Fact]
    public void AnUndrawableCharacter_IsKeptWhenTheMarkIsAlsoMissing()
    {
        // The fail-safe. A font without U+25A1 would otherwise trade one
        // missing glyph for another, which fixes nothing and loses the
        // original.
        var prepared = RenderableText.Prepare("a 一 b", CoveringAsciiAnd());

        Assert.Equal("a 一 b", prepared.Text);
        Assert.Equal(1, prepared.Unrenderable[0x4E00]);
    }

    [Fact]
    public void TheEmbeddedFontHasTheMark()
    {
        // Everything above is fixture-stated. If Liberation Sans lacked
        // U+25A1 the fail-safe would engage in production and #287 would be
        // unfixed while its tests passed.
        var prepared = RenderableText.Prepare("一", EmbeddedFont.Coverage);

        Assert.Equal("□", prepared.Text);
    }

    [Fact]
    public void UnknownCoverage_ChangesNothingAtAll()
    {
        var prepared = RenderableText.Prepare("hormone‑blocking", FontGlyphCoverage.Read([0, 1, 2]));

        Assert.Equal("hormone‑blocking", prepared.Text);
        Assert.Empty(prepared.Unrenderable);
        // #302: and counts nothing either way. Both being empty here is the
        // ambiguity the issue is about — it reads exactly like a document that
        // needed nothing, which is why the caller must report the status too.
        Assert.Empty(prepared.Folded);
    }

    // #302: the fold is the case that goes well, and used to leave no trace.
    // Its frequency is the only evidence for how close real prose comes to
    // needing a stand-in that does not exist, which is what #287 turns on.

    [Fact]
    public void AFoldedCharacter_IsCounted()
    {
        var prepared = RenderableText.Prepare("a‑b‑c", CoveringAsciiAnd(0x2010));

        Assert.Equal("a‐b‐c", prepared.Text);
        Assert.Equal(2, prepared.Folded[0x2011]);
        Assert.Empty(prepared.Unrenderable);
    }

    [Fact]
    public void ARemovedZeroWidthCharacter_CountsAsAFold()
    {
        // Removal is a substitution whose replacement is nothing. It changes
        // the delivered bytes, so it is counted like any other.
        var prepared = RenderableText.Prepare("a​b", CoveringAsciiAnd());

        Assert.Equal("ab", prepared.Text);
        Assert.Equal(1, prepared.Folded[0x200B]);
    }

    [Fact]
    public void AFoldAndAnUndrawableCharacter_AreCountedApart()
    {
        // The distinction that matters: one was handled, one was not, and a
        // single combined count would say neither.
        var prepared = RenderableText.Prepare("a‑b 一", CoveringAsciiAnd(0x2010));

        Assert.Equal(1, prepared.Folded[0x2011]);
        Assert.Equal(1, prepared.Unrenderable[0x4E00]);
        Assert.DoesNotContain(0x4E00, prepared.Folded.Keys);
        Assert.DoesNotContain(0x2011, prepared.Unrenderable.Keys);
    }

    [Fact]
    public void ACharacterTheFontHas_IsNeitherFoldedNorCounted()
    {
        // The control: the substitution is conditional on the gap, so a font
        // that has U+2011 must produce an empty fold count rather than a
        // no-op recorded as work.
        var prepared = RenderableText.Prepare("a‑b", CoveringAsciiAnd(0x2011, 0x2010));

        Assert.Equal("a‑b", prepared.Text);
        Assert.Empty(prepared.Folded);
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
/// <summary>
/// The real Liberation Sans coverage, read from the assembly the way
/// production reads it. Everything else here states coverage rather than
/// reading it, which is right for testing the rule and wrong for the one
/// question only the shipped font can answer.
/// </summary>
internal static class EmbeddedFont
{
    internal static readonly FontGlyphCoverage Coverage = Read();

    private static FontGlyphCoverage Read()
    {
        using var stream = typeof(ConsultDocumentPdf).Assembly
            .GetManifestResourceStream("Consultologist.Api.Fonts.LiberationSans-Regular.ttf");
        Assert.NotNull(stream);

        using var memory = new MemoryStream();
        stream!.CopyTo(memory);
        return FontGlyphCoverage.Read(memory.ToArray());
    }
}

internal static class FakeFont
{
    /// <summary>
    /// A cmap whose only subtable is one this reader does not handle (#302).
    /// Distinct from a font with no cmap at all, and the two used to be
    /// indistinguishable once both had failed.
    /// </summary>
    internal static FontGlyphCoverage WithOnlyANonFormat4Subtable()
    {
        var sub = new List<byte>();
        void U16(int v) { sub.Add((byte)(v >> 8)); sub.Add((byte)v); }

        U16(12);                      // format — segmented coverage, not read here
        U16(0); U16(0); U16(0);       // padding the reader never interprets

        return FontGlyphCoverage.Read(Wrap("cmap"u8.ToArray(), CmapAround(sub.ToArray())));
    }

    /// <summary>A table directory that names no cmap at all (#302).</summary>
    internal static FontGlyphCoverage WithoutACmapTable() =>
        FontGlyphCoverage.Read(Wrap("glyf"u8.ToArray(), [0, 0, 0, 0]));

    /// <summary>Wraps one table in the minimum sfnt a reader needs to find it.</summary>
    private static byte[] Wrap(byte[] tag, byte[] table)
    {
        var font = new List<byte>();
        void F16(int v) { font.Add((byte)(v >> 8)); font.Add((byte)v); }
        void F32(long v) { font.Add((byte)(v >> 24)); font.Add((byte)(v >> 16)); font.Add((byte)(v >> 8)); font.Add((byte)v); }

        F32(0x00010000);              // sfnt version
        F16(1);                       // numTables
        F16(0); F16(0); F16(0);       // searchRange, entrySelector, rangeShift
        font.AddRange(tag);
        F32(0);                       // checksum
        F32(28);                      // offset — 12 header + 16 record
        F32(table.Length);
        font.AddRange(table);

        return font.ToArray();
    }

    /// <summary>The cmap header and single encoding record around a subtable.</summary>
    private static byte[] CmapAround(byte[] subtable)
    {
        var cmap = new List<byte>();
        void C16(int v) { cmap.Add((byte)(v >> 8)); cmap.Add((byte)v); }
        void C32(long v) { cmap.Add((byte)(v >> 24)); cmap.Add((byte)(v >> 16)); cmap.Add((byte)(v >> 8)); cmap.Add((byte)v); }

        C16(0); C16(1);               // version, numTables
        C16(3); C16(1); C32(12);      // platform 3, encoding 1, offset 12
        cmap.AddRange(subtable);

        return cmap.ToArray();
    }

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

        return FontGlyphCoverage.Read(Wrap("cmap"u8.ToArray(), CmapAround(sub.ToArray())));
    }
}
