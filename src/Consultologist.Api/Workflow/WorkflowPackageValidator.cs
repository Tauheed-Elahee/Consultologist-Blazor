using System.Reflection;
using Scriban;
using Scriban.Runtime;

namespace Consultologist.Api.Workflow;

/// <summary>
/// Validates a specVersion-2 or -3 package per docs/customizable-workflow/
/// package-format-v2.md and package-format-v3.md. Used at load time by the store
/// (the engine's enforcement point) and by tests; the same checks apply at publish time.
/// </summary>
public static class WorkflowPackageValidator
{
    /// <summary>The Scriban version this engine renders with (Major.Minor.Patch).</summary>
    public static readonly Version EngineScribanVersion = GetScribanVersion();

    public sealed record ValidationResult(List<string> Errors, List<string> Warnings)
    {
        public bool IsValid => Errors.Count == 0;
    }

    /// <param name="files">Package-relative path → file content, for every file the manifest references.</param>
    public static ValidationResult Validate(
        WorkflowPackageManifest manifest,
        IReadOnlyDictionary<string, string> files)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (manifest.Templating is null
            || !string.Equals(manifest.Templating.Engine, "scriban", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"templating.engine must be 'scriban' for specVersion {manifest.SpecVersion}.");
        }
        else if (!Version.TryParse(manifest.Templating.EngineVersion, out var engineVersion))
        {
            errors.Add($"templating.engineVersion '{manifest.Templating.EngineVersion}' is not a valid version.");
        }
        else if (engineVersion > EngineScribanVersion)
        {
            errors.Add(
                $"templating.engineVersion {engineVersion} is newer than this engine's Scriban {EngineScribanVersion}.");
        }

        var prompts = manifest.Prompts ?? new List<WorkflowPromptSpec>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var isV3 = manifest.SpecVersion >= 3;

        foreach (var prompt in prompts)
        {
            if (!ids.Add(prompt.Id))
            {
                errors.Add($"Duplicate prompt id '{prompt.Id}'.");
            }

            if (!isV3 && !WorkflowPromptContract.RequiredPromptIds.Contains(prompt.Id))
            {
                errors.Add($"Unknown prompt id '{prompt.Id}' (the prompt set is closed in specVersion 2).");
                continue;
            }

            // Analysis prompts keep the closed variable contract in every specVersion;
            // in v3 the prose prompts' variable names are free-form (covered by bindings).
            if (!isV3 || WorkflowPromptContract.AnalysisPromptIds.Contains(prompt.Id))
            {
                foreach (var variable in prompt.Variables.Where(v => !WorkflowPromptContract.KnownVariables.Contains(v)))
                {
                    errors.Add($"Prompt '{prompt.Id}' declares variable '{variable}' which is not in the variable contract.");
                }
            }

            if (prompt.Prelude != null && (manifest.Preludes is null || !manifest.Preludes.ContainsKey(prompt.Prelude)))
            {
                errors.Add($"Prompt '{prompt.Id}' references undefined prelude '{prompt.Prelude}'.");
            }

            if (!files.TryGetValue(prompt.File, out var templateText))
            {
                errors.Add($"Prompt '{prompt.Id}' file '{prompt.File}' is missing from the package.");
                continue;
            }

            ValidateTemplate(prompt, templateText, errors, warnings);
        }

        var requiredIds = isV3 ? WorkflowPromptContract.AnalysisPromptIds : WorkflowPromptContract.RequiredPromptIds;
        foreach (var missing in requiredIds.Except(ids))
        {
            errors.Add($"Required prompt id '{missing}' is missing from the manifest.");
        }

        foreach (var (preludeId, preludePath) in manifest.Preludes ?? new Dictionary<string, string>())
        {
            if (!files.ContainsKey(preludePath))
            {
                errors.Add($"Prelude '{preludeId}' file '{preludePath}' is missing from the package.");
            }
        }

        if (isV3)
        {
            ValidateSectionSteps(manifest, errors);
        }
        else if (manifest.SectionSteps is { Count: > 0 })
        {
            errors.Add("sectionSteps requires specVersion 3.");
        }

        return new ValidationResult(errors, warnings);
    }

    private static void ValidateSectionSteps(WorkflowPackageManifest manifest, List<string> errors)
    {
        var steps = manifest.SectionSteps ?? new List<WorkflowSectionStepSpec>();
        if (steps.Count == 0)
        {
            errors.Add("sectionSteps is required and must not be empty in specVersion 3.");
            return;
        }

        var promptsById = (manifest.Prompts ?? new List<WorkflowPromptSpec>())
            .GroupBy(p => p.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var stepIds = new HashSet<string>(StringComparer.Ordinal);
        var referencedPrompts = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];

            if (!stepIds.Add(step.StepId))
            {
                errors.Add($"Duplicate section step id '{step.StepId}' (give reused prompts an explicit 'id').");
            }

            if (string.IsNullOrWhiteSpace(step.Label))
            {
                errors.Add($"Section step '{step.StepId}' has no label.");
            }

            foreach (var source in step.Bindings.Values.Where(s => !WorkflowStepBindingSources.All.Contains(s)))
            {
                errors.Add($"Section step '{step.StepId}' binds to unknown source '{source}'.");
            }

            if (i == 0 && step.Bindings.Values.Contains(WorkflowStepBindingSources.PreviousStepOutput))
            {
                errors.Add($"Section step '{step.StepId}' is first and cannot bind '{WorkflowStepBindingSources.PreviousStepOutput}'.");
            }

            if (!promptsById.TryGetValue(step.Prompt, out var prompt))
            {
                errors.Add($"Section step '{step.StepId}' references undeclared prompt '{step.Prompt}'.");
                continue;
            }

            referencedPrompts.Add(step.Prompt);

            var declared = new HashSet<string>(prompt.Variables, StringComparer.Ordinal);
            if (!declared.SetEquals(step.Bindings.Keys))
            {
                errors.Add(
                    $"Section step '{step.StepId}' bindings [{string.Join(", ", step.Bindings.Keys.Order(StringComparer.Ordinal))}] " +
                    $"must exactly match prompt '{step.Prompt}' variables [{string.Join(", ", prompt.Variables.Order(StringComparer.Ordinal))}].");
            }
        }

        foreach (var orphan in promptsById.Keys
                     .Where(id => !WorkflowPromptContract.AnalysisPromptIds.Contains(id) && !referencedPrompts.Contains(id)))
        {
            errors.Add($"Prompt '{orphan}' is neither an analysis prompt nor referenced by any section step.");
        }
    }

    private static void ValidateTemplate(
        WorkflowPromptSpec prompt,
        string templateText,
        List<string> errors,
        List<string> warnings)
    {
        var template = Template.Parse(templateText);
        if (template.HasErrors)
        {
            errors.Add($"Prompt '{prompt.Id}' template does not parse: {string.Join("; ", template.Messages)}");
            return;
        }

        // Undeclared-variable use: render in strict mode against exactly the declared
        // variables with placeholder values; any other access throws.
        try
        {
            var probe = new ScriptObject();
            foreach (var variable in prompt.Variables)
            {
                probe.Add(variable, "placeholder");
            }

            var context = new TemplateContext { StrictVariables = true };
            context.PushGlobal(probe);
            template.Render(context);
        }
        catch (Exception ex)
        {
            errors.Add($"Prompt '{prompt.Id}' failed strict rendering with its declared variables: {ex.Message}");
        }

        // Unused-declaration heuristic (warning only): the variable name never appears
        // in the template text.
        foreach (var variable in prompt.Variables.Where(v => !templateText.Contains(v, StringComparison.Ordinal)))
        {
            warnings.Add($"Prompt '{prompt.Id}' declares variable '{variable}' but the template never mentions it.");
        }
    }

    private static Version GetScribanVersion()
    {
        var assembly = typeof(Template).Assembly;

        // NuGet packages commonly pin AssemblyVersion to Major.0.0; the real package
        // version is in the informational version (possibly with +metadata/-prerelease).
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (informational != null
            && Version.TryParse(informational.Split('+', '-')[0], out var packageVersion))
        {
            return new Version(packageVersion.Major, packageVersion.Minor, Math.Max(packageVersion.Build, 0));
        }

        var version = assembly.GetName().Version ?? new Version(0, 0, 0);
        return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
    }
}
