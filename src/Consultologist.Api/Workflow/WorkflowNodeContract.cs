namespace Consultologist.Api.Workflow;

/// <summary>
/// The specVersion-5 node vocabulary: binding-reference parsing, the closed
/// concept-renderer set, and the engine defaults. Parsing is namespace-syntactic;
/// vocabulary closures (collection-declared item fields, declared data entries)
/// belong to the validator. Pre-v5 vocabularies (map kinds, previous_step_output,
/// input:sections, the synthesis shims) were retired by the v5-only rebase —
/// see docs/customizable-workflow/package-format-v5.md.
/// </summary>
public abstract record WorkflowNodeBindingSource
{
    public sealed record Input(string Name) : WorkflowNodeBindingSource;
    public sealed record NodeOutput(string NodeId) : WorkflowNodeBindingSource;
    public sealed record Item(string Field) : WorkflowNodeBindingSource;
    public sealed record Data(string Id) : WorkflowNodeBindingSource;
}

public static class WorkflowNodeBindingSources
{
    public const string InputConsultDraft = "input:consult_draft";
    public const string ItemName = "item:name";
    public const string NodePrefix = "node:";
    public const string DataPrefix = "data:";

    private static readonly IReadOnlySet<string> InputNames = new HashSet<string>(StringComparer.Ordinal) { "consult_draft" };

    public static bool TryParse(string raw, out WorkflowNodeBindingSource? source, out string? error)
    {
        source = null;
        error = null;

        var separator = raw.IndexOf(':');
        if (separator <= 0 || separator == raw.Length - 1)
        {
            error = $"unrecognized source '{raw}'";
            return false;
        }

        var ns = raw[..separator];
        var name = raw[(separator + 1)..];

        switch (ns)
        {
            case "input" when InputNames.Contains(name):
                source = new WorkflowNodeBindingSource.Input(name);
                return true;
            case "input":
                error = $"unknown input '{name}' (expected consult_draft)";
                return false;
            case "item":
                source = new WorkflowNodeBindingSource.Item(name);
                return true;
            case "data":
                source = new WorkflowNodeBindingSource.Data(name);
                return true;
            case "node":
                source = new WorkflowNodeBindingSource.NodeOutput(name);
                return true;
            default:
                error = $"unrecognized source '{raw}'";
                return false;
        }
    }
}

public static class WorkflowConceptRenderers
{
    public const string ConceptBullets = "concept-bullets";
    public const string ConceptContext = "concept-context";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        ConceptBullets,
        ConceptContext
    };
}

public static class WorkflowNodeDefaults
{
    /// <summary>
    /// Legacy concept source stamps for the four canonical analysis node ids: the
    /// concept-context rendering embeds "source:", and these strings predate node
    /// ids — byte parity for the canonical workflow requires preserving them.
    /// Custom node ids fall back to the node id itself.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> WellKnownConceptSources = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["extract-patient-concepts"] = "patient",
        ["identify-problem"] = "problem",
        ["create-typical-trajectory"] = "typical-trajectory",
        ["create-patient-trajectory"] = "patient-trajectory"
    };

    public const string ConceptListSchemaId = "concept-list";
}
