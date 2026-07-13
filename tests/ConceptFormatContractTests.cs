using Consultologist.Api.Agents;
using Consultologist.Api.Jobs;
using Consultologist.Api.Models;

namespace Consultologist.Api.Tests;

/// <summary>
/// Verifies the two concept rendering formats normative in package-format-v2.md,
/// byte-for-byte — these strings feed the model, so any harness must reproduce them.
/// </summary>
public class ConceptFormatContractTests
{
    private static readonly IReadOnlyList<ClinicalConcept> Sample = new[]
    {
        new ClinicalConcept("Malignant neoplasm of breast", "disorder", "254837009", true, true, "draft"),
        new ClinicalConcept("Diabetes mellitus", "disorder", "73211009", true, false, "draft"),
        new ClinicalConcept("Family support strong", "", "", false, false, "draft"),
        new ClinicalConcept("Tamoxifen therapy", "procedure", "75367002", true, true, "typical", "adjuvant endocrine therapy")
    };

    [Fact]
    public void AnalysisFormat_MatchesSpec()
    {
        var expected =
            "- Malignant neoplasm of breast (disorder) - 254837009" + Environment.NewLine +
            "- Diabetes mellitus (disorder) - 73211009 [not active SNOMED concept]" + Environment.NewLine +
            "- Family support strong [not SNOMED concept]" + Environment.NewLine +
            "- Tamoxifen therapy (procedure) - 75367002";

        Assert.Equal(expected, ConsultGenerationConceptFormatter.Format(Sample));
    }

    [Fact]
    public void AnalysisFormat_EmptyListRendersNone()
    {
        Assert.Equal("(none)", ConsultGenerationConceptFormatter.Format(Array.Empty<ClinicalConcept>()));
    }

    [Fact]
    public void TrajectoryContextFormat_MatchesSpec()
    {
        var expected =
            "- Malignant neoplasm of breast (disorder) - 254837009; active: True; source: draft" + Environment.NewLine +
            "- Diabetes mellitus (disorder) - 73211009; active: False; source: draft" + Environment.NewLine +
            "- Family support strong () - ; active: False; source: draft" + Environment.NewLine +
            "- Tamoxifen therapy (procedure) - 75367002; active: True; source: typical support: adjuvant endocrine therapy";

        Assert.Equal(expected, AgentSectionGenerator.FormatConcepts(Sample));
    }

    [Fact]
    public void TrajectoryContextFormat_EmptyListRendersNone()
    {
        Assert.Equal("(none)", AgentSectionGenerator.FormatConcepts(Array.Empty<ClinicalConcept>()));
    }
}

