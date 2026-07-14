namespace Consultologist.Api.Workflow;

/// <summary>
/// The specVersion-4 node vocabulary: binding-reference parsing, node kinds, the
/// closed concept-renderer set, and the synthesis that lets one interpreter path serve
/// every spec version. See docs/customizable-workflow/package-format-v4.md.
/// </summary>
public abstract record WorkflowNodeBindingSource
{
    public sealed record Input(string Name) : WorkflowNodeBindingSource;
    public sealed record NodeOutput(string NodeId) : WorkflowNodeBindingSource;
    public sealed record Item(string Field) : WorkflowNodeBindingSource;
    public sealed record PreviousStepOutput : WorkflowNodeBindingSource;
}

public static class WorkflowNodeBindingSources
{
    public const string InputConsultDraft = "input:consult_draft";
    public const string InputSections = "input:sections";
    public const string ItemName = "item:name";
    public const string ItemStandard = "item:standard";
    public const string ItemId = "item:id";
    public const string PreviousStepOutput = "previous_step_output";
    public const string NodePrefix = "node:";

    private static readonly IReadOnlySet<string> InputNames = new HashSet<string>(StringComparer.Ordinal) { "consult_draft", "sections" };
    private static readonly IReadOnlySet<string> ItemFields = new HashSet<string>(StringComparer.Ordinal) { "name", "standard", "id" };

    public static bool TryParse(string raw, out WorkflowNodeBindingSource? source, out string? error)
    {
        source = null;
        error = null;

        if (string.Equals(raw, PreviousStepOutput, StringComparison.Ordinal))
        {
            source = new WorkflowNodeBindingSource.PreviousStepOutput();
            return true;
        }

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
                error = $"unknown input '{name}' (expected consult_draft or sections)";
                return false;
            case "item" when ItemFields.Contains(name):
                source = new WorkflowNodeBindingSource.Item(name);
                return true;
            case "item":
                error = $"unknown item field '{name}' (expected name, standard, or id)";
                return false;
            case "node":
                source = new WorkflowNodeBindingSource.NodeOutput(name);
                return true;
            default:
                error = $"unrecognized source '{raw}'";
                return false;
        }
    }
}

public static class WorkflowNodeKinds
{
    public const string Prompt = "prompt";
    public const string Map = "map";
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
    /// Legacy concept source stamps for the four canonical analysis nodes: the
    /// concept-context rendering embeds "source:", and these strings predate node ids
    /// — byte parity for existing packages requires preserving them. Custom node ids
    /// fall back to the node id itself.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> WellKnownConceptSources = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [WorkflowPromptContract.ExtractPatientConcepts] = "patient",
        [WorkflowPromptContract.IdentifyProblem] = "problem",
        [WorkflowPromptContract.CreateTypicalTrajectory] = "typical-trajectory",
        [WorkflowPromptContract.CreatePatientTrajectory] = "patient-trajectory"
    };

    public const string ConceptListSchemaId = "concept-list";
    public const string SectionsMapNodeId = "sections";

    /// <summary>
    /// The DAG the engine synthesizes for specVersion 2 and 3 packages, which predate
    /// manifest-declared nodes: the four canonical analysis nodes (labels, bindings,
    /// and failIfEmpty messages moved verbatim from the pre-milestone-4 compiled
    /// pipeline) plus one map node over the package's declared or synthesized section
    /// steps. Normative in package-format-v4.md.
    /// </summary>
    public static IReadOnlyList<WorkflowNodeSpec> V3SynthesizedDag(IReadOnlyList<WorkflowSectionStepSpec> sectionSteps)
    {
        return new[]
        {
            new WorkflowNodeSpec(
                WorkflowPromptContract.ExtractPatientConcepts,
                WorkflowNodeKinds.Prompt,
                "Extracting clinical concepts",
                Prompt: WorkflowPromptContract.ExtractPatientConcepts,
                Bindings: new Dictionary<string, WorkflowBindingValue>(StringComparer.Ordinal)
                {
                    [WorkflowPromptContract.ConsultDraft] = new(WorkflowNodeBindingSources.InputConsultDraft)
                },
                Output: new WorkflowNodeOutputSpec(
                    ConceptListSchemaId,
                    "The consult could not be processed because clinical concepts could not be extracted from the draft.")),

            new WorkflowNodeSpec(
                WorkflowPromptContract.IdentifyProblem,
                WorkflowNodeKinds.Prompt,
                "Identifying primary problem",
                Prompt: WorkflowPromptContract.IdentifyProblem,
                Bindings: new Dictionary<string, WorkflowBindingValue>(StringComparer.Ordinal)
                {
                    [WorkflowPromptContract.PatientConcepts] = new($"node:{WorkflowPromptContract.ExtractPatientConcepts}")
                },
                Output: new WorkflowNodeOutputSpec(
                    ConceptListSchemaId,
                    "No valid disease or problem concept was identified.")),

            new WorkflowNodeSpec(
                WorkflowPromptContract.CreateTypicalTrajectory,
                WorkflowNodeKinds.Prompt,
                "Building reference trajectory",
                Prompt: WorkflowPromptContract.CreateTypicalTrajectory,
                Bindings: new Dictionary<string, WorkflowBindingValue>(StringComparer.Ordinal)
                {
                    [WorkflowPromptContract.ProblemConcepts] = new($"node:{WorkflowPromptContract.IdentifyProblem}")
                },
                Output: new WorkflowNodeOutputSpec(
                    ConceptListSchemaId,
                    "No valid typical trajectory concepts were generated.")),

            new WorkflowNodeSpec(
                WorkflowPromptContract.CreatePatientTrajectory,
                WorkflowNodeKinds.Prompt,
                "Building patient trajectory",
                Prompt: WorkflowPromptContract.CreatePatientTrajectory,
                Bindings: new Dictionary<string, WorkflowBindingValue>(StringComparer.Ordinal)
                {
                    [WorkflowPromptContract.ProblemConcepts] = new($"node:{WorkflowPromptContract.IdentifyProblem}"),
                    [WorkflowPromptContract.PatientConcepts] = new($"node:{WorkflowPromptContract.ExtractPatientConcepts}"),
                    [WorkflowPromptContract.TypicalTrajectoryConcepts] = new($"node:{WorkflowPromptContract.CreateTypicalTrajectory}")
                },
                Output: new WorkflowNodeOutputSpec(
                    ConceptListSchemaId,
                    "No valid patient trajectory concepts were generated.")),

            new WorkflowNodeSpec(
                SectionsMapNodeId,
                WorkflowNodeKinds.Map,
                "Generating sections",
                Over: WorkflowNodeBindingSources.InputSections,
                Steps: sectionSteps.Select(step => new WorkflowMapStepSpec(
                    step.Prompt,
                    step.Label,
                    step.Bindings.ToDictionary(
                        pair => pair.Key,
                        pair => RaiseStepBinding(pair.Value),
                        StringComparer.Ordinal),
                    step.Id)).ToList())
        };
    }

    /// <summary>
    /// Lowers a specVersion-4 map node's steps to the v3 section-step shape, so the
    /// existing run-prose-step activity executes map bodies unchanged. Bijective with
    /// RaiseStepBinding under the v4.0 map-binding closure.
    /// </summary>
    public static IReadOnlyList<WorkflowSectionStepSpec> LowerMapSteps(WorkflowNodeSpec mapNode)
    {
        return (mapNode.Steps ?? new List<WorkflowMapStepSpec>())
            .Select(step => new WorkflowSectionStepSpec(
                step.Prompt,
                step.Label,
                step.Bindings.ToDictionary(
                    pair => pair.Key,
                    pair => LowerStepBinding(pair.Value),
                    StringComparer.Ordinal),
                step.Id))
            .ToList();
    }

    private static WorkflowBindingValue RaiseStepBinding(string v3Source) => v3Source switch
    {
        WorkflowStepBindingSources.SectionName => new(WorkflowNodeBindingSources.ItemName),
        WorkflowStepBindingSources.SectionStandard => new(WorkflowNodeBindingSources.ItemStandard),
        WorkflowStepBindingSources.ConsultDraft => new(WorkflowNodeBindingSources.InputConsultDraft),
        WorkflowStepBindingSources.PreviousStepOutput => new(WorkflowNodeBindingSources.PreviousStepOutput),
        WorkflowStepBindingSources.PatientTrajectoryConcepts => new(
            $"node:{WorkflowPromptContract.CreatePatientTrajectory}",
            WorkflowConceptRenderers.ConceptContext),
        _ => throw new InvalidOperationException($"Unknown v3 step binding source '{v3Source}'.")
    };

    private static string LowerStepBinding(WorkflowBindingValue binding) => binding.From switch
    {
        WorkflowNodeBindingSources.ItemName => WorkflowStepBindingSources.SectionName,
        WorkflowNodeBindingSources.ItemStandard => WorkflowStepBindingSources.SectionStandard,
        WorkflowNodeBindingSources.InputConsultDraft => WorkflowStepBindingSources.ConsultDraft,
        WorkflowNodeBindingSources.PreviousStepOutput => WorkflowStepBindingSources.PreviousStepOutput,
        _ when binding.From.StartsWith(WorkflowNodeBindingSources.NodePrefix, StringComparison.Ordinal)
            => WorkflowStepBindingSources.PatientTrajectoryConcepts,
        _ => throw new InvalidOperationException($"Map step binding '{binding.From}' cannot be lowered to a v3 step source.")
    };
}
