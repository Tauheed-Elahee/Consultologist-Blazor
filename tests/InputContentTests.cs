using Consultologist.Api.Jobs;
using Consultologist.Api.Models;
using Consultologist.Api.Workflow;

namespace Consultologist.Api.Tests;

/// <summary>
/// #290: the floor that stops a consult being generated from a referral that
/// never arrived.
///
/// The two directions are not symmetric, and these tests are weighted
/// accordingly. Refusing a real terse referral is annoying, visible and
/// recoverable. Letting an empty one through produced a complete oncology
/// consult in which every section read "not documented" — delivered, with
/// nothing signalling it.
/// </summary>
public class InputContentTests
{
    /// <summary>The case that must always generate.</summary>
    private const string TerseReferral =
        "65M, newly diagnosed adenocarcinoma of the lung, stage IIIA, for consideration of chemoradiation. PMHx HTN.";

    /// <summary>What #290's message actually carried.</summary>
    private const string LinkOnlyBody =
        "https://consultologist-my.sharepoint.com/:w:/g/personal/user_consultologist_ai/EX9fLk2mQ_dHqB7wZ8vNc1kBqL3rT6yPmA2sK4uW0nXeVg";

    [Fact]
    public void ATerseButRealReferral_Counts()
    {
        // The regression that matters most in the other direction.
        Assert.True(InputContent.MeaningfulLength(TerseReferral) >= InputContent.MinimumCharacters);
    }

    [Fact]
    public void ABodyThatIsOnlyALink_CountsAsNothing()
    {
        // Longer than the terse referral in raw characters, which is exactly
        // why raw length cannot be the rule.
        Assert.True(LinkOnlyBody.Length > TerseReferral.Length);
        Assert.Equal(0, InputContent.MeaningfulLength(LinkOnlyBody));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t  ")]
    public void NothingAtAll_CountsAsNothing(string? text)
    {
        Assert.Equal(0, InputContent.MeaningfulLength(text));
    }

    [Theory]
    [InlineData("https://example.com/a")]
    [InlineData("http://example.com/a")]
    [InlineData("www.example.com/a")]
    [InlineData("ftp://example.com/a")]
    [InlineData("mailto:someone@example.com")]
    public void EveryLinkFormAMailClientProduces_IsStripped(string link)
    {
        Assert.Equal(0, InputContent.MeaningfulLength(link));
    }

    [Fact]
    public void ProseAroundALink_StillCounts()
    {
        // A clinician may legitimately include a link beside a real referral.
        var text = $"See {LinkOnlyBody} — {TerseReferral}";

        Assert.True(InputContent.MeaningfulLength(text) >= InputContent.MinimumCharacters);
    }

    [Fact]
    public void WhitespaceIsNotContent()
    {
        // Forty spaces must not clear a forty-character floor.
        Assert.Equal(0, InputContent.MeaningfulLength(new string(' ', 100)));
    }

    // --- which input gets named ---

    private static WorkflowPackageManifest V7() => V7Fixtures.MultiDeliverable();

    [Fact]
    public void V7_ARequiredInputWithoutContent_IsNamed()
    {
        var offending = InputContent.FindInputWithoutContent(
            new ConsultGenerationRequest(null),
            V7(),
            new Dictionary<string, string> { ["consult_draft"] = LinkOnlyBody, ["prior_notes"] = TerseReferral },
            40);

        Assert.Equal("consult_draft", offending);
    }

    [Fact]
    public void V7_AnOptionalInputWithoutContent_IsNotNamed()
    {
        // prior_notes is optional; absent or thin is its normal state and must
        // never block a consult.
        var offending = InputContent.FindInputWithoutContent(
            new ConsultGenerationRequest(null),
            V7(),
            new Dictionary<string, string> { ["consult_draft"] = TerseReferral, ["prior_notes"] = "" },
            40);

        Assert.Null(offending);
    }

    [Fact]
    public void V5_TheDraftIsTheInput()
    {
        var offending = InputContent.FindInputWithoutContent(
            new ConsultGenerationRequest(LinkOnlyBody),
            V5Fixtures.Manifest(),
            null,
            40);

        Assert.Equal("consult_draft", offending);
    }

    [Fact]
    public void V5_ARealDraftPasses()
    {
        Assert.Null(InputContent.FindInputWithoutContent(
            new ConsultGenerationRequest(TerseReferral),
            V5Fixtures.Manifest(),
            null,
            40));
    }

    [Fact]
    public void AZeroMinimum_DisablesTheFloorEntirely()
    {
        // The escape hatch, if the floor ever turns out to be wrong in
        // production and has to be dropped without a deploy.
        Assert.Null(InputContent.FindInputWithoutContent(
            new ConsultGenerationRequest(""),
            V5Fixtures.Manifest(),
            null,
            0));
    }
}
