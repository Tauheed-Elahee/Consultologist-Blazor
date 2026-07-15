using System.Text.Json;
using Consultologist.Api.Workflow;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Consultologist.Api.Tests;

public static class V2Fixtures
{
    public static WorkflowPackageManifest Manifest(string engineVersion = "7.2.5") => new(
        "general",
        "v2026.08.1",
        2,
        new WorkflowTemplatingSpec("scriban", engineVersion),
        new Dictionary<string, string> { ["snomed-tool-guidance"] = "prompts/_snomed-tool-guidance.md" },
        new List<WorkflowPromptSpec>
        {
            new(WorkflowPromptContract.ExtractPatientConcepts, "prompts/extract-patient-concepts.md",
                new List<string> { WorkflowPromptContract.ConsultDraft }, "snomed-tool-guidance"),
            new(WorkflowPromptContract.IdentifyProblem, "prompts/identify-problem.md",
                new List<string> { WorkflowPromptContract.PatientConcepts }, "snomed-tool-guidance"),
            new(WorkflowPromptContract.CreateTypicalTrajectory, "prompts/create-typical-trajectory.md",
                new List<string> { WorkflowPromptContract.ProblemConcepts }, "snomed-tool-guidance"),
            new(WorkflowPromptContract.CreatePatientTrajectory, "prompts/create-patient-trajectory.md",
                new List<string>
                {
                    WorkflowPromptContract.ProblemConcepts,
                    WorkflowPromptContract.PatientConcepts,
                    WorkflowPromptContract.TypicalTrajectoryConcepts
                }, "snomed-tool-guidance"),
            new(WorkflowPromptContract.StandardSectionDraft, "prompts/standard-section-draft.md",
                new List<string> { WorkflowPromptContract.SectionName, WorkflowPromptContract.PatientTrajectoryConcepts }),
            new(WorkflowPromptContract.PatientSectionDraft, "prompts/patient-section-draft.md",
                new List<string>
                {
                    WorkflowPromptContract.StandardSectionDraftVariable,
                    WorkflowPromptContract.ConsultDraft,
                    WorkflowPromptContract.SectionName
                }),
            new(WorkflowPromptContract.SectionInstructions, "prompts/section-instructions.md",
                new List<string>
                {
                    WorkflowPromptContract.PatientSectionDraftVariable,
                    WorkflowPromptContract.SectionName,
                    WorkflowPromptContract.SectionStandard
                })
        });

    public static Dictionary<string, string> Files(WorkflowPackageManifest manifest)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["prompts/_snomed-tool-guidance.md"] = "Use short SNOMED search terms."
        };

        foreach (var prompt in manifest.Prompts!)
        {
            files[prompt.File] = string.Join(
                "\n\n",
                prompt.Variables.Select(v => $"{v} block:\n{{{{ {v} }}}}"));
        }

        return files;
    }
}

public class WorkflowManifestV2Tests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void ManifestV2_Deserializes()
    {
        const string json = """
            {
              "name": "general",
              "version": "v2026.08.1",
              "specVersion": 2,
              "templating": { "engine": "scriban", "engineVersion": "7.2.5" },
              "preludes": { "snomed-tool-guidance": "prompts/_snomed-tool-guidance.md" },
              "prompts": [
                { "id": "extract-patient-concepts", "file": "prompts/extract-patient-concepts.md",
                  "variables": ["consult_draft"], "prelude": "snomed-tool-guidance" }
              ]
            }
            """;

        var manifest = JsonSerializer.Deserialize<WorkflowPackageManifest>(json, JsonOptions)!;

        Assert.Equal(2, manifest.SpecVersion);
        Assert.Equal("scriban", manifest.Templating!.Engine);
        Assert.Equal("7.2.5", manifest.Templating.EngineVersion);
        Assert.Single(manifest.Prompts!);
        Assert.Equal("snomed-tool-guidance", manifest.Prompts![0].Prelude);
        Assert.Equal(new[] { "consult_draft" }, manifest.Prompts[0].Variables);
    }

    [Fact]
    public void ManifestV1_StillDeserializes_WithoutPromptFields()
    {
        const string json = """{ "name": "general", "version": "v2026.07.2", "specVersion": 1 }""";

        var manifest = JsonSerializer.Deserialize<WorkflowPackageManifest>(json, JsonOptions)!;

        Assert.Equal(1, manifest.SpecVersion);
        Assert.Null(manifest.Templating);
        Assert.Null(manifest.Prompts);
    }
}

public class WorkflowPackageValidatorTests
{
    [Fact]
    public void Validate_PassesForWellFormedPackage()
    {
        var manifest = V2Fixtures.Manifest();

        var result = WorkflowPackageValidator.Validate(manifest, V2Fixtures.Files(manifest), TestOutputContracts.CatalogSchemas);

        Assert.True(result.IsValid, string.Join(" | ", result.Errors));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Validate_RejectsMissingRequiredPromptId()
    {
        var manifest = V2Fixtures.Manifest();
        var files = V2Fixtures.Files(manifest);
        manifest = manifest with { Prompts = manifest.Prompts!.Where(p => p.Id != WorkflowPromptContract.IdentifyProblem).ToList() };

        var result = WorkflowPackageValidator.Validate(manifest, files, TestOutputContracts.CatalogSchemas);

        Assert.Contains(result.Errors, e => e.Contains("identify-problem") && e.Contains("missing"));
    }

    [Fact]
    public void Validate_RejectsUnknownPromptId()
    {
        var manifest = V2Fixtures.Manifest();
        var files = V2Fixtures.Files(manifest);
        manifest.Prompts!.Add(new WorkflowPromptSpec("my-custom-step", "prompts/custom.md", new List<string>()));

        var result = WorkflowPackageValidator.Validate(manifest, files, TestOutputContracts.CatalogSchemas);

        Assert.Contains(result.Errors, e => e.Contains("my-custom-step") && e.Contains("closed"));
    }

    [Fact]
    public void Validate_RejectsVariableOutsideContract()
    {
        var manifest = V2Fixtures.Manifest();
        var files = V2Fixtures.Files(manifest);
        manifest.Prompts![0].Variables.Add("patient_age");

        var result = WorkflowPackageValidator.Validate(manifest, files, TestOutputContracts.CatalogSchemas);

        Assert.Contains(result.Errors, e => e.Contains("patient_age") && e.Contains("contract"));
    }

    [Fact]
    public void Validate_RejectsTemplateUsingUndeclaredVariable()
    {
        var manifest = V2Fixtures.Manifest();
        var files = V2Fixtures.Files(manifest);
        files["prompts/extract-patient-concepts.md"] = "{{ consult_draft }} and {{ section_name }}";

        var result = WorkflowPackageValidator.Validate(manifest, files, TestOutputContracts.CatalogSchemas);

        Assert.Contains(result.Errors, e => e.Contains("extract-patient-concepts") && e.Contains("strict"));
    }

    [Fact]
    public void Validate_WarnsOnDeclaredButUnusedVariable()
    {
        var manifest = V2Fixtures.Manifest();
        var files = V2Fixtures.Files(manifest);
        files["prompts/extract-patient-concepts.md"] = "No variables here at all.";

        var result = WorkflowPackageValidator.Validate(manifest, files, TestOutputContracts.CatalogSchemas);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("consult_draft") && w.Contains("never mentions"));
    }

    [Fact]
    public void Validate_RejectsEngineVersionNewerThanEngine()
    {
        var manifest = V2Fixtures.Manifest(engineVersion: "99.0.0");

        var result = WorkflowPackageValidator.Validate(manifest, V2Fixtures.Files(manifest), TestOutputContracts.CatalogSchemas);

        Assert.Contains(result.Errors, e => e.Contains("newer than this engine"));
    }

    [Fact]
    public void Validate_RejectsMissingPromptFile()
    {
        var manifest = V2Fixtures.Manifest();
        var files = V2Fixtures.Files(manifest);
        files.Remove("prompts/identify-problem.md");

        var result = WorkflowPackageValidator.Validate(manifest, files, TestOutputContracts.CatalogSchemas);

        Assert.Contains(result.Errors, e => e.Contains("prompts/identify-problem.md") && e.Contains("missing"));
    }
}

public class PromptTemplateRendererTests
{
    private static WorkflowPromptTemplate Template(string text, string? prelude = null) =>
        new("extract-patient-concepts", text, new[] { "consult_draft" }, prelude);

    [Fact]
    public void Render_InterpolatesVariables()
    {
        var result = PromptTemplateRenderer.Render(
            Template("Draft:\n{{ consult_draft }}"),
            new Dictionary<string, string> { ["consult_draft"] = "Patient draft text." });

        Assert.Equal("Draft:\nPatient draft text.", result);
    }

    [Fact]
    public void Render_PrependsPreludeWithBlankLine()
    {
        var result = PromptTemplateRenderer.Render(
            Template("{{ consult_draft }}", prelude: "Guidance text.\n"),
            new Dictionary<string, string> { ["consult_draft"] = "x" });

        Assert.Equal("Guidance text.\n\nx", result);
    }

    [Fact]
    public void Render_ThrowsWhenSuppliedVariablesDoNotMatchDeclared()
    {
        var extra = new Dictionary<string, string> { ["consult_draft"] = "x", ["section_name"] = "HPI" };
        var missing = new Dictionary<string, string>();

        Assert.Throws<InvalidOperationException>(() => PromptTemplateRenderer.Render(Template("{{ consult_draft }}"), extra));
        Assert.Throws<InvalidOperationException>(() => PromptTemplateRenderer.Render(Template("{{ consult_draft }}"), missing));
    }

    [Fact]
    public void Render_ThrowsOnUndeclaredVariableAccess_StrictMode()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PromptTemplateRenderer.Render(
            Template("{{ consult_draft }} {{ not_declared }}"),
            new Dictionary<string, string> { ["consult_draft"] = "x" }));

        Assert.Contains("failed to render", ex.Message);
    }
}

public class WorkflowPromptProviderTests
{
    private static WorkflowPromptProvider CreateProvider(IWorkflowPackageStore store) =>
        new(store, NullLogger<WorkflowPromptProvider>.Instance);

    private static readonly Dictionary<string, string> Vars = new() { ["consult_draft"] = "draft" };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a valid ref")]
    public async Task Render_Throws_WithoutUsableRef(string? packageRef)
    {
        var store = Substitute.For<IWorkflowPackageStore>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateProvider(store).RenderAsync(
            packageRef, WorkflowPromptContract.ExtractPatientConcepts, Vars, CancellationToken.None));

        await store.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default);
    }

    [Fact]
    public async Task Render_Throws_ForSpecVersion1Package()
    {
        var store = Substitute.For<IWorkflowPackageStore>();
        store.ResolveAsync(Arg.Any<WorkflowPackageRef>(), Arg.Any<CancellationToken>())
            .Returns(new WorkflowPackage(new WorkflowPackageManifest("general", "v2026.07.2", 1), "## hpi: HPI"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateProvider(store).RenderAsync(
            "general@v2026.07.2", WorkflowPromptContract.ExtractPatientConcepts, Vars, CancellationToken.None));

        Assert.Contains("predates prompt templates", ex.Message);
    }

    [Fact]
    public async Task Render_Throws_ForMissingPromptId()
    {
        var store = Substitute.For<IWorkflowPackageStore>();
        var template = new WorkflowPromptTemplate(
            WorkflowPromptContract.IdentifyProblem,
            "{{ patient_concepts }}",
            new[] { "patient_concepts" },
            null);
        store.ResolveAsync(Arg.Any<WorkflowPackageRef>(), Arg.Any<CancellationToken>())
            .Returns(new WorkflowPackage(
                V2Fixtures.Manifest(),
                "## hpi: HPI",
                new Dictionary<string, WorkflowPromptTemplate> { [template.Id] = template }));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateProvider(store).RenderAsync(
            "general@v2026.08.1", WorkflowPromptContract.ExtractPatientConcepts, Vars, CancellationToken.None));

        Assert.Contains("has no prompt", ex.Message);
    }

    [Fact]
    public async Task Render_RendersFromV2Package()
    {
        var template = new WorkflowPromptTemplate(
            WorkflowPromptContract.ExtractPatientConcepts,
            "Extract from: {{ consult_draft }}",
            new[] { "consult_draft" },
            "Prelude.");
        var package = new WorkflowPackage(
            V2Fixtures.Manifest(),
            "## hpi: HPI",
            new Dictionary<string, WorkflowPromptTemplate> { [template.Id] = template });
        var store = Substitute.For<IWorkflowPackageStore>();
        store.ResolveAsync(Arg.Any<WorkflowPackageRef>(), Arg.Any<CancellationToken>()).Returns(package);

        var result = await CreateProvider(store).RenderAsync(
            "general@v2026.08.1", WorkflowPromptContract.ExtractPatientConcepts, Vars, CancellationToken.None);

        Assert.Equal("Prelude.\n\nExtract from: draft", result);
    }
}
