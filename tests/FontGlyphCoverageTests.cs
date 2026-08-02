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
    private static readonly FontGlyphCoverage Coverage = ReadEmbeddedFont();

    private static FontGlyphCoverage ReadEmbeddedFont()
    {
        using var stream = typeof(ConsultDocumentPdf).Assembly
            .GetManifestResourceStream("Consultologist.Api.Fonts.LiberationSans-Regular.ttf");
        Assert.NotNull(stream);

        using var memory = new MemoryStream();
        stream!.CopyTo(memory);
        return FontGlyphCoverage.Read(memory.ToArray());
    }

    [Fact]
    public void TheEmbeddedFontParses()
    {
        // If this fails everything else is vacuous: an unknown coverage
        // reports every character as drawable.
        Assert.False(Coverage.IsUnknown);
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
    }
}
