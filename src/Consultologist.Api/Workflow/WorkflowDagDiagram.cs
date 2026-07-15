using System.Text;

namespace Consultologist.Api.Workflow;

/// <summary>
/// Derives a Mermaid flowchart from a specVersion-4 manifest's nodes. Bindings are
/// the edges plus strictly more, so the diagram is always generated and never
/// authored — the derived-view rule that rejected a second edge file
/// (docs/customizable-workflow/output-contract-catalog.md). The checked-in
/// packages/general/dag.mmd is pinned to this generator by a snapshot test.
/// </summary>
public static class WorkflowDagDiagram
{
    public static string Generate(WorkflowPackageManifest manifest)
    {
        var nodes = manifest.Nodes
            ?? throw new InvalidOperationException(
                $"Package {manifest.Name}@{manifest.Version} declares no nodes; the DAG diagram requires specVersion 4.");

        var sb = new StringBuilder();
        sb.Append("flowchart TD\n");

        // Engine-supplied inputs first, in first-reference order.
        foreach (var input in CollectInputSources(nodes))
        {
            sb.Append($"    {Sanitize(input)}([\"{input}\"])\n");
        }

        sb.Append('\n');

        foreach (var node in nodes.Where(n => n.Kind == WorkflowNodeKinds.Prompt))
        {
            var label = $"{node.Id}<br/>{node.Label}";
            if (node.Output != null)
            {
                label += $"<br/>output: {node.Output.Schema}";
            }

            sb.Append($"    {Sanitize(node.Id)}[\"{label}\"]\n");

            foreach (var (variable, binding) in node.Bindings ?? new Dictionary<string, WorkflowBindingValue>())
            {
                AppendBindingEdge(sb, binding, variable, Sanitize(node.Id));
            }
        }

        foreach (var node in nodes.Where(n => n.Kind == WorkflowNodeKinds.Map))
        {
            sb.Append('\n');
            var mapId = Sanitize(node.Id);

            if (node.Over != null)
            {
                sb.Append($"    {Sanitize(node.Over)} -->|\"over\"| {mapId}\n");
            }

            sb.Append($"    subgraph {mapId}[\"{node.Id} — {node.Label} (per item)\"]\n");

            var steps = node.Steps ?? new List<WorkflowMapStepSpec>();
            var stepIds = steps.Select(step => $"{mapId}_{Sanitize(step.StepId)}").ToList();

            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                var itemFields = step.Bindings
                    .Where(pair => pair.Value.From.StartsWith("item:", StringComparison.Ordinal))
                    .Select(pair => pair.Value.From["item:".Length..])
                    .ToList();
                var label = $"{step.StepId}<br/>{step.Label}";
                if (itemFields.Count > 0)
                {
                    label += $"<br/>item: {string.Join(", ", itemFields)}";
                }

                sb.Append($"        {stepIds[i]}[\"{label}\"]\n");
            }

            for (var i = 0; i < steps.Count; i++)
            {
                foreach (var (variable, binding) in steps[i].Bindings)
                {
                    if (binding.From == WorkflowNodeBindingSources.PreviousStepOutput)
                    {
                        sb.Append($"        {stepIds[i - 1]} -->|\"{variable}\"| {stepIds[i]}\n");
                    }
                }
            }

            sb.Append("    end\n");

            // Edges crossing into the subgraph (upstream nodes and inputs) are emitted
            // outside it so Mermaid keeps the subgraph box tight.
            for (var i = 0; i < steps.Count; i++)
            {
                foreach (var (variable, binding) in steps[i].Bindings)
                {
                    AppendBindingEdge(sb, binding, variable, stepIds[i]);
                }
            }
        }

        return sb.ToString();
    }

    private static void AppendBindingEdge(StringBuilder sb, WorkflowBindingValue binding, string variable, string targetId)
    {
        string sourceId;

        if (binding.From.StartsWith(WorkflowNodeBindingSources.NodePrefix, StringComparison.Ordinal))
        {
            sourceId = Sanitize(binding.From[WorkflowNodeBindingSources.NodePrefix.Length..]);
        }
        else if (binding.From.StartsWith("input:", StringComparison.Ordinal))
        {
            sourceId = Sanitize(binding.From);
        }
        else
        {
            return; // item:* lives in the step label; previous_step_output is the chain edge.
        }

        var label = binding.As is null ? variable : $"{variable} (as {binding.As})";
        sb.Append($"    {sourceId} -->|\"{label}\"| {targetId}\n");
    }

    private static IReadOnlyList<string> CollectInputSources(IReadOnlyList<WorkflowNodeSpec> nodes)
    {
        var inputs = new List<string>();

        void Add(string? from)
        {
            if (from != null && from.StartsWith("input:", StringComparison.Ordinal) && !inputs.Contains(from))
            {
                inputs.Add(from);
            }
        }

        foreach (var node in nodes)
        {
            foreach (var binding in (node.Bindings ?? new Dictionary<string, WorkflowBindingValue>()).Values)
            {
                Add(binding.From);
            }

            Add(node.Over);

            foreach (var step in node.Steps ?? new List<WorkflowMapStepSpec>())
            {
                foreach (var binding in step.Bindings.Values)
                {
                    Add(binding.From);
                }
            }
        }

        return inputs;
    }

    private static string Sanitize(string raw)
        => new(raw.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}
