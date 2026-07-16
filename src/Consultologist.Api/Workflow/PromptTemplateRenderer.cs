using Scriban;
using Scriban.Runtime;

namespace Consultologist.Api.Workflow;

/// <summary>
/// Renders a prompt template in strict mode: exactly the declared
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
