using Microsoft.Extensions.Logging;
using Scriban;
using Scriban.Runtime;

namespace Consultologist.Api.Workflow;

/// <summary>
/// Renders a specVersion-2 prompt template in strict mode: exactly the declared
/// variables are supplied, any other access throws, and the prelude (if any) is
/// prepended followed by one blank line.
/// </summary>
public static class PromptTemplateRenderer
{
    public static string Render(WorkflowPromptTemplate prompt, IReadOnlyDictionary<string, string> variables)
    {
        var declared = new HashSet<string>(prompt.Variables, StringComparer.Ordinal);
        if (!declared.SetEquals(variables.Keys))
        {
            throw new InvalidOperationException(
                $"Prompt '{prompt.Id}' expects exactly [{string.Join(", ", prompt.Variables)}] " +
                $"but was supplied [{string.Join(", ", variables.Keys)}].");
        }

        var template = Template.Parse(prompt.TemplateText);
        if (template.HasErrors)
        {
            throw new InvalidOperationException(
                $"Prompt '{prompt.Id}' template does not parse: {string.Join("; ", template.Messages)}");
        }

        string rendered;
        try
        {
            var scriptObject = new ScriptObject();
            foreach (var (name, value) in variables)
            {
                scriptObject.Add(name, value);
            }

            var context = new TemplateContext { StrictVariables = true };
            context.PushGlobal(scriptObject);
            rendered = template.Render(context);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Prompt '{prompt.Id}' failed to render: {ex.Message}", ex);
        }

        return string.IsNullOrEmpty(prompt.PreludeText)
            ? rendered
            : $"{prompt.PreludeText.TrimEnd()}\n\n{rendered}";
    }
}

public interface IWorkflowPromptProvider
{
    /// <summary>
    /// Renders the prompt from the job's pinned package. Returns null when the compiled
    /// default should be used instead (no package ref, unparseable ref, or a
    /// specVersion-1 package). Throws when a v2 package is present but rendering fails —
    /// fail loud per the format spec.
    /// </summary>
    Task<string?> TryRenderAsync(
        string? packageRef,
        string promptId,
        IReadOnlyDictionary<string, string> variables,
        CancellationToken cancellationToken);
}

public sealed class WorkflowPromptProvider : IWorkflowPromptProvider
{
    private readonly IWorkflowPackageStore _packageStore;
    private readonly ILogger<WorkflowPromptProvider> _logger;

    public WorkflowPromptProvider(IWorkflowPackageStore packageStore, ILogger<WorkflowPromptProvider> logger)
    {
        _packageStore = packageStore;
        _logger = logger;
    }

    public async Task<string?> TryRenderAsync(
        string? packageRef,
        string promptId,
        IReadOnlyDictionary<string, string> variables,
        CancellationToken cancellationToken)
    {
        if (!WorkflowPackageRef.TryParse(packageRef, out var parsedRef))
        {
            if (!string.IsNullOrWhiteSpace(packageRef))
            {
                _logger.LogWarning(
                    "Job carries an unparseable workflow package ref '{PackageRef}'; using compiled prompts.",
                    packageRef);
            }

            return null;
        }

        var package = await _packageStore.ResolveAsync(parsedRef!, cancellationToken);

        if (!package.HasPrompts)
        {
            return null; // specVersion 1: compiled prompts remain the defaults.
        }

        if (!package.Prompts!.TryGetValue(promptId, out var prompt))
        {
            // A validated v2 package always carries the closed set; absence here means
            // the package bypassed validation — fail loud rather than silently fall back.
            throw new InvalidOperationException(
                $"Workflow package {package.Ref} (specVersion {package.Manifest.SpecVersion}) has no prompt '{promptId}'.");
        }

        return PromptTemplateRenderer.Render(prompt, variables);
    }
}
