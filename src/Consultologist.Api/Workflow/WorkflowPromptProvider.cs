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
    /// Renders the prompt from the job's pinned package. A usable specVersion-2+
    /// package is mandatory (milestone 3 retired the compiled fallbacks): a missing or
    /// unparseable ref, a specVersion-1 package, or a rendering failure all throw —
    /// fail loud per the format spec.
    /// </summary>
    Task<string> RenderAsync(
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

    public async Task<string> RenderAsync(
        string? packageRef,
        string promptId,
        IReadOnlyDictionary<string, string> variables,
        CancellationToken cancellationToken)
    {
        if (!WorkflowPackageRef.TryParse(packageRef, out var parsedRef))
        {
            throw new InvalidOperationException(
                $"Prompt '{promptId}' has no usable workflow package ref ('{packageRef}').");
        }

        var package = await _packageStore.ResolveAsync(parsedRef!, cancellationToken);

        if (!package.HasPrompts)
        {
            throw new InvalidOperationException(
                $"Workflow package {package.Ref} (specVersion {package.Manifest.SpecVersion}) predates prompt templates; a specVersion 2 or newer package is required.");
        }

        if (!package.Prompts!.TryGetValue(promptId, out var prompt))
        {
            throw new InvalidOperationException(
                $"Workflow package {package.Ref} (specVersion {package.Manifest.SpecVersion}) has no prompt '{promptId}'.");
        }

        return PromptTemplateRenderer.Render(prompt, variables);
    }
}
