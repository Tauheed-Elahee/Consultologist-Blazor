namespace Consultologist.Api.Tests;

/// <summary>
/// The one canonicalisation applied to text before it is hashed or compared
/// (#251, docs/DOCUMENT_INPUT.md § 2).
///
/// Its own file because the rule having been written out four times is what
/// let two of them drift from it, and filing its tests under whichever caller
/// happened to need them next is how that starts. The callers keep their own
/// tests for the behaviour they owe — see
/// <c>DocumentExtractionTests.ALoneCarriageReturn_IsALineEndingToo</c>,
/// <c>ConsultGenerationJobStarterTests.BareCrText_HashesLikeItsLfEquivalent</c>
/// and <c>AgentAttestationTests.Compare_ToleratesALoneCarriageReturn</c>,
/// which together pin that all three sites really do route through here.
/// </summary>
public class CanonicalTextTests
{
    [Theory]
    [InlineData("One.\r\nTwo.", "One.\nTwo.")]
    [InlineData("One.\rTwo.", "One.\nTwo.")]
    [InlineData("One.\nTwo.", "One.\nTwo.")]
    // Mixed endings in one document — what a file edited on two platforms
    // looks like, and the case a single blanket replacement gets wrong.
    [InlineData("One.\r\nTwo.\rThree.\nFour.", "One.\nTwo.\nThree.\nFour.")]
    public void EveryLineEnding_BecomesLf(string input, string expected)
    {
        Assert.Equal(expected, CanonicalText.Normalize(input));
    }

    [Fact]
    public void CrlfIsNotTurnedIntoABlankLine()
    {
        // Order is the rule: replacing a lone CR first would make every \r\n
        // into \n\n and invent a blank line between every pair.
        Assert.Equal("One.\nTwo.", CanonicalText.Normalize("One.\r\nTwo."));
    }

    [Theory]
    [InlineData("One.\n\n\n", "One.")]
    [InlineData("One.  \t ", "One.")]
    // Trailing whitespace goes wholesale, which is why this is not called
    // LineEndings. Interior whitespace is untouched: line structure,
    // hyphenation and hard wrapping are meaning, per § 2.
    [InlineData("One.  \n  Two.  ", "One.  \n  Two.")]
    public void TrailingWhitespaceGoes_AndInteriorWhitespaceStays(string input, string expected)
    {
        Assert.Equal(expected, CanonicalText.Normalize(input));
    }

    [Fact]
    public void NullStaysNull()
    {
        // Not cosmetic. The job starter runs this over ConsultDraft, and
        // {"consultDraft":null} and {"consultDraft":""} are different
        // canonical JSON, so collapsing one to the other would move the v2
        // draft-only hash. No shipped path reaches it — this holds an
        // invariant rather than fixing a live defect.
        Assert.Null(CanonicalText.Normalize(null));
    }

    [Fact]
    public void SeparatorsThatAreNotLineEndings_AreLeftAlone()
    {
        // Deliberately no wider than line endings: U+2028, U+0085 and the
        // vertical tab have no documented way of arriving here, and removing
        // them would be a semantic edit to clinical text rather than a
        // canonicalisation of it.
        //
        // Escapes rather than the characters themselves: a raw U+2028 in a
        // source literal is invisible in review.
        const string separators = "One.\u2028Two.\u0085Three.\u000BFour.";

        Assert.Equal(separators, CanonicalText.Normalize(separators));
    }
}
