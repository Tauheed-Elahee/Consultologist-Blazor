using System.Text.Json;
using Consultologist.Api.Agents;
using Consultologist.Api.Jobs;
using Consultologist.Api.Models;
using Consultologist.Api.Workflow;

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

/// <summary>
/// Renders the repo's actual packages/general templates with fixed inputs and asserts
/// byte-equality with the compiled fallback prompts. Guards the port baseline: while
/// the package's prompt content is intended to match the compiled defaults, any drift
/// between the two paths is a bug. When package prompts are deliberately evolved past
/// the compiled baseline, update or retire the affected case here consciously.
/// Template files end with a trailing newline that the compiled C# raw strings lack;
/// comparison is modulo trailing whitespace.
/// </summary>
public class PromptParityTests
{
    private const string Draft = "62-year-old woman with newly diagnosed left breast invasive ductal carcinoma.";
    private const string SectionName = "History of Present Illness";
    private const string Standard = "Chronological prose narrative. Open with a one-sentence orientation.";
    private const string StandardDraft = "The patient presents with a newly diagnosed breast malignancy.";
    private const string PatientDraft = "Emily Lee is a 62-year-old woman with left breast invasive ductal carcinoma.";

    private static readonly string AnalysisConcepts = ConsultGenerationConceptFormatter.Format(new[]
    {
        new ClinicalConcept("Malignant neoplasm of breast", "disorder", "254837009", true, true, "draft"),
        new ClinicalConcept("Family support strong", "", "", false, false, "draft")
    });

    private static readonly string TrajectoryConcepts = AgentSectionGenerator.FormatConcepts(new[]
    {
        new ClinicalConcept("Malignant neoplasm of breast", "disorder", "254837009", true, true, "draft")
    });

    private static readonly Lazy<IReadOnlyDictionary<string, WorkflowPromptTemplate>> Templates = new(LoadRepoPackageTemplates);

    private static IReadOnlyDictionary<string, WorkflowPromptTemplate> LoadRepoPackageTemplates()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Consultologist.sln")))
        {
            dir = dir.Parent;
        }

        var packageDir = Path.Combine(dir!.FullName, "packages", "general");
        var manifest = JsonSerializer.Deserialize<WorkflowPackageManifest>(
            File.ReadAllText(Path.Combine(packageDir, "manifest.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        string ReadPackageFile(string relative) =>
            File.ReadAllText(Path.Combine(packageDir, relative.Replace('/', Path.DirectorySeparatorChar)));

        return manifest.Prompts!.ToDictionary(
            prompt => prompt.Id,
            prompt => new WorkflowPromptTemplate(
                prompt.Id,
                ReadPackageFile(prompt.File),
                prompt.Variables,
                prompt.Prelude is null ? null : ReadPackageFile(manifest.Preludes![prompt.Prelude])),
            StringComparer.Ordinal);
    }

    private static string Render(string promptId, Dictionary<string, string> variables) =>
        PromptTemplateRenderer.Render(Templates.Value[promptId], variables);

    private static void AssertParity(string expectedCompiled, string renderedFromPackage) =>
        Assert.Equal(expectedCompiled.TrimEnd(), renderedFromPackage.TrimEnd());

    [Fact]
    public void ExtractPatientConcepts_Parity()
    {
        AssertParity(
            ConsultGenerationCompiledPrompts.ExtractPatientConcepts(Draft),
            Render(WorkflowPromptContract.ExtractPatientConcepts, new()
            {
                [WorkflowPromptContract.ConsultDraft] = Draft
            }));
    }

    [Fact]
    public void IdentifyProblem_Parity()
    {
        AssertParity(
            ConsultGenerationCompiledPrompts.IdentifyProblem(AnalysisConcepts),
            Render(WorkflowPromptContract.IdentifyProblem, new()
            {
                [WorkflowPromptContract.PatientConcepts] = AnalysisConcepts
            }));
    }

    [Fact]
    public void CreateTypicalTrajectory_Parity()
    {
        AssertParity(
            ConsultGenerationCompiledPrompts.CreateTypicalTrajectory(AnalysisConcepts),
            Render(WorkflowPromptContract.CreateTypicalTrajectory, new()
            {
                [WorkflowPromptContract.ProblemConcepts] = AnalysisConcepts
            }));
    }

    [Fact]
    public void CreatePatientTrajectory_Parity()
    {
        AssertParity(
            ConsultGenerationCompiledPrompts.CreatePatientTrajectory(AnalysisConcepts, AnalysisConcepts, AnalysisConcepts),
            Render(WorkflowPromptContract.CreatePatientTrajectory, new()
            {
                [WorkflowPromptContract.ProblemConcepts] = AnalysisConcepts,
                [WorkflowPromptContract.PatientConcepts] = AnalysisConcepts,
                [WorkflowPromptContract.TypicalTrajectoryConcepts] = AnalysisConcepts
            }));
    }

    [Fact]
    public void StandardSectionDraft_Parity()
    {
        AssertParity(
            AgentSectionGenerator.BuildStandardSectionDraftMessage(
                new[] { new ClinicalConcept("Malignant neoplasm of breast", "disorder", "254837009", true, true, "draft") },
                SectionName),
            Render(WorkflowPromptContract.StandardSectionDraft, new()
            {
                [WorkflowPromptContract.SectionName] = SectionName,
                [WorkflowPromptContract.PatientTrajectoryConcepts] = TrajectoryConcepts
            }));
    }

    [Fact]
    public void PatientSectionDraft_Parity()
    {
        AssertParity(
            AgentSectionGenerator.BuildPatientSectionDraftMessage(StandardDraft, Draft, SectionName),
            Render(WorkflowPromptContract.PatientSectionDraft, new()
            {
                [WorkflowPromptContract.StandardSectionDraftVariable] = StandardDraft,
                [WorkflowPromptContract.ConsultDraft] = Draft,
                [WorkflowPromptContract.SectionName] = SectionName
            }));
    }

    [Theory]
    [InlineData(Standard)]
    [InlineData("")]
    [InlineData("   ")]
    public void SectionInstructions_Parity_BothConditionalBranches(string sectionStandard)
    {
        AssertParity(
            AgentSectionGenerator.BuildSectionInstructionsMessage(PatientDraft, SectionName, sectionStandard),
            Render(WorkflowPromptContract.SectionInstructions, new()
            {
                [WorkflowPromptContract.PatientSectionDraftVariable] = PatientDraft,
                [WorkflowPromptContract.SectionName] = SectionName,
                [WorkflowPromptContract.SectionStandard] = sectionStandard
            }));
    }
}
