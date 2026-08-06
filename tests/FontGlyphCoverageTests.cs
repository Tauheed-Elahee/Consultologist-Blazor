using Consultologist.Api.Email;

namespace Consultologist.Api.Tests;

/// <summary>
/// #252: the cmap reader, against the font actually embedded in the assembly.
///
/// The expectations were established independently — by parsing the same TTF
/// with a separate implementation before this one existed — so a bug shared
/// between reader and test cannot make them agree.
/// </summary>
public class FontGlyphCoverageTests
{
    private static FontGlyphCoverage Coverage => EmbeddedFont.Coverage;

    [Fact]
    public void TheEmbeddedFontParses()
    {
        // If this fails everything else is vacuous: an unknown coverage
        // reports every character as drawable.
        Assert.False(Coverage.IsUnknown);
        Assert.Equal(FontCoverageStatus.Read, Coverage.Status);
    }

    [Fact]
    public void LiberationSansHasNoNonBreakingHyphen()
    {
        // The whole defect. U+2011 renders as .notdef, and readers copy that
        // out as a control character.
        Assert.False(Coverage.Covers(0x2011));
    }

    [Theory]
    [InlineData(0x002D, "HYPHEN-MINUS")]
    [InlineData(0x2010, "HYPHEN")]           // the stand-in for U+2011
    [InlineData(0x2013, "EN DASH")]
    [InlineData(0x2014, "EM DASH")]          // used by the thematic-break rule
    [InlineData(0x2019, "RIGHT SINGLE QUOTATION MARK")]
    [InlineData(0x2022, "BULLET")]           // used by the list renderer
    [InlineData(0x03BC, "GREEK SMALL LETTER MU")]
    [InlineData(0x2264, "LESS-THAN OR EQUAL TO")]
    [InlineData(0x2212, "MINUS SIGN")]
    public void CharactersClinicalProseActuallyUsesArePresent(int codepoint, string name)
    {
        Assert.True(Coverage.Covers(codepoint), $"U+{codepoint:X4} {name} should be drawable.");
    }

    [Fact]
    public void AFontItCannotParse_ReportsEverythingAsDrawable()
    {
        // Fails open on purpose: substituting text we did not need to would
        // be a silent edit of clinical prose, which is worse than the defect.
        var nonsense = FontGlyphCoverage.Read([0, 1, 2, 3]);

        Assert.True(nonsense.IsUnknown);
        Assert.True(nonsense.Covers(0x2011));
        Assert.Equal(FontCoverageStatus.ParseFailed, nonsense.Status);
    }

    // #302: every one of these leaves the fold inert — nothing substituted,
    // nothing counted — which is indistinguishable from a document that needed
    // nothing. They are separated so a production warning names a cause an
    // operator can act on, rather than "coverage is unknown".

    [Fact]
    public void NoFontBytesAtAll_IsItsOwnStatus()
    {
        // The resolver returning nothing is the caller's failure, not a
        // property of any font. It used to reach the fail-open by handing an
        // empty array to the parser and relying on it to throw.
        var missing = FontGlyphCoverage.Missing();

        Assert.True(missing.IsUnknown);
        Assert.True(missing.Covers(0x2011));
        Assert.Equal(FontCoverageStatus.FontMissing, missing.Status);
    }

    [Fact]
    public void AFontWithNoCmapTable_SaysThatIsWhy()
    {
        var coverage = FakeFont.WithoutACmapTable();

        Assert.True(coverage.IsUnknown);
        Assert.Equal(FontCoverageStatus.NoCmapTable, coverage.Status);
    }

    [Fact]
    public void AFontWithNoFormat4Subtable_SaysThatIsWhy()
    {
        // A real possibility rather than a contrivance: a font may carry only
        // a format-12 subtable, which this reader does not handle.
        var coverage = FakeFont.WithOnlyANonFormat4Subtable();

        Assert.True(coverage.IsUnknown);
        Assert.Equal(FontCoverageStatus.NoFormat4Subtable, coverage.Status);
    }

    [Fact]
    public void ACmapThatMapsNothing_SaysThatIsWhy()
    {
        // Parsed successfully and learned nothing. Reported as unknown rather
        // than as "this font draws nothing", which would fold every character
        // in the document.
        var coverage = FakeFont.WithCoverage();

        Assert.True(coverage.IsUnknown);
        Assert.Equal(FontCoverageStatus.NoCodepointsMapped, coverage.Status);
    }
}
