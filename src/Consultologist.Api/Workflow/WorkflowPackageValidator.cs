using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scriban;
using Scriban.Runtime;

namespace Consultologist.Api.Workflow;

/// <summary>
/// Validates a specVersion-2, -3, or -4 package per docs/customizable-workflow/
/// package-format-v2.md, -v3.md, and -v4.md. Used at load time by the store
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
    /// <param name="catalogSchemas">
    /// Output-contract id → schema JSON from the engine's catalog: every declared
    /// package schema must canonically match one of these (the closure that welds
    /// package contracts to attested agents, output-contract-catalog.md).
    /// </param>
    public static ValidationResult Validate(
        WorkflowPackageManifest manifest,
        IReadOnlyDictionary<string, string> files,
        IReadOnlyDictionary<string, string> catalogSchemas)
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
        var isV4 = manifest.SpecVersion >= 4;
        var isV5 = manifest.SpecVersion >= 5;

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

            // v2 keeps the closed variable contract for every prompt; v3 keeps it for
            // the analysis prompts only; in v4 the analysis closure lifts — variable
            // names are free-form everywhere, covered by per-node binding rules.
            if (!isV3 || (!isV4 && WorkflowPromptContract.AnalysisPromptIds.Contains(prompt.Id)))
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

        // v4 has no required prompt ids — node references define what must exist.
        var requiredIds = isV4
            ? Enumerable.Empty<string>()
            : isV3 ? WorkflowPromptContract.AnalysisPromptIds : WorkflowPromptContract.RequiredPromptIds;
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

        if (!isV5)
        {
            if (manifest.DerivedFrom != null)
            {
                errors.Add("derivedFrom requires specVersion 5.");
            }

            if (manifest.Data is { Count: > 0 })
            {
                errors.Add("data requires specVersion 5.");
            }

            if (manifest.Result != null)
            {
                errors.Add("result requires specVersion 5.");
            }

            foreach (var node in (manifest.Nodes ?? new List<WorkflowNodeSpec>()).Where(n => n.ForEach != null))
            {
                errors.Add($"Node '{node.Id}' forEach requires specVersion 5.");
            }
        }

        if (isV5)
        {
            if (manifest.SectionSteps is { Count: > 0 })
            {
                errors.Add("sectionSteps was replaced by nodes in specVersion 4.");
            }

            ValidateDerivedFrom(manifest, errors);
            ValidateV5Nodes(manifest, files, catalogSchemas, errors);
        }
        else if (isV4)
        {
            if (manifest.SectionSteps is { Count: > 0 })
            {
                errors.Add("sectionSteps was replaced by nodes in specVersion 4.");
            }

            ValidateNodes(manifest, files, catalogSchemas, errors);
        }
        else
        {
            if (manifest.Nodes is { Count: > 0 })
            {
                errors.Add("nodes requires specVersion 4.");
            }

            if (isV3)
            {
                ValidateSectionSteps(manifest, errors);
            }
            else if (manifest.SectionSteps is { Count: > 0 })
            {
                errors.Add("sectionSteps requires specVersion 3.");
            }
        }

        return new ValidationResult(errors, warnings);
    }

    private static void ValidateDerivedFrom(WorkflowPackageManifest manifest, List<string> errors)
    {
        // Absent and null are equivalent: a root package. When set, the fork origin
        // must be a concrete immutable version — @latest would make lineage mutable.
        if (manifest.DerivedFrom is null)
        {
            return;
        }

        if (!WorkflowPackageRef.TryParse(manifest.DerivedFrom, out var origin))
        {
            errors.Add($"derivedFrom '{manifest.DerivedFrom}' is not a valid package reference.");
            return;
        }

        if (origin!.IsLatest)
        {
            errors.Add("derivedFrom must be a concrete version (name@vYYYY.MM.N), never @latest.");
        }
    }

    /// <summary>
    /// The specVersion-5 node rules: one kind, forEach multiplicity, the relational
    /// edge semantics (item-aligned / broadcast allowed; aggregate and
    /// cross-collection closed), collection-declared item fields, and the result
    /// contract. See docs/customizable-workflow/package-format-v5.md.
    /// </summary>
    private static void ValidateV5Nodes(
        WorkflowPackageManifest manifest,
        IReadOnlyDictionary<string, string> files,
        IReadOnlyDictionary<string, string> catalogSchemas,
        List<string> errors)
    {
        var data = WorkflowDataResolver.Resolve(manifest, files, errors);
        var nodes = manifest.Nodes ?? new List<WorkflowNodeSpec>();

        if (nodes.Count == 0)
        {
            errors.Add("nodes is required and must not be empty in specVersion 5.");
            return;
        }

        var promptsById = (manifest.Prompts ?? new List<WorkflowPromptSpec>())
            .GroupBy(p => p.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var nodesById = new Dictionary<string, WorkflowNodeSpec>(StringComparer.Ordinal);
        var promptReferenceCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var edges = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var node in nodes)
        {
            if (!nodesById.TryAdd(node.Id, node))
            {
                errors.Add($"Duplicate node id '{node.Id}'.");
            }
        }

        var conceptListNodeIds = nodes
            .Where(n => n.Output != null)
            .Select(n => n.Id)
            .ToHashSet(StringComparer.Ordinal);

        void CheckBinding(WorkflowNodeSpec node, string variable, WorkflowBindingValue binding)
        {
            if (!WorkflowNodeBindingSources.TryParse(binding.From, out var source, out var parseError))
            {
                errors.Add($"Node '{node.Id}' binds '{variable}' to {parseError}.");
                return;
            }

            switch (source)
            {
                case WorkflowNodeBindingSource.Input { Name: "sections" }:
                    errors.Add($"Node '{node.Id}' binds '{variable}' to 'input:sections', which is removed in specVersion 5 (sections are package data).");
                    return;
                case WorkflowNodeBindingSource.PreviousStepOutput:
                    errors.Add($"Node '{node.Id}' binds '{variable}' to 'previous_step_output', which is removed in specVersion 5 (bind an item-aligned node: edge instead).");
                    return;
                case WorkflowNodeBindingSource.Item item when node.ForEach is null:
                    errors.Add($"Node '{node.Id}' binds '{variable}' to 'item:{item.Field}' but declares no forEach.");
                    return;
                case WorkflowNodeBindingSource.Item item:
                    if (TryResolveForEachCollection(node, data, out _, out var collection)
                        && !collection!.Fields.Contains(item.Field))
                    {
                        errors.Add($"Node '{node.Id}' binds '{variable}' to unknown item field '{item.Field}' (the collection declares: {string.Join(", ", collection.Fields)}).");
                    }

                    return;
                case WorkflowNodeBindingSource.Data dataSource:
                    if (data.Collections.ContainsKey(dataSource.Id))
                    {
                        errors.Add($"Node '{node.Id}' binds '{variable}' to data collection '{dataSource.Id}', which is only iterable via forEach.");
                    }
                    else if (!data.Scalars.ContainsKey(dataSource.Id))
                    {
                        errors.Add($"Node '{node.Id}' binds '{variable}' to unknown data entry '{dataSource.Id}'.");
                    }

                    return;
                case WorkflowNodeBindingSource.NodeOutput target:
                    if (!nodesById.TryGetValue(target.NodeId, out var targetNode))
                    {
                        errors.Add($"Node '{node.Id}' binds '{variable}' to unknown node '{target.NodeId}'.");
                        return;
                    }

                    // The edge-semantics table: aggregate and cross-collection closed.
                    if (targetNode.ForEach != null && node.ForEach is null)
                    {
                        errors.Add($"Node '{node.Id}' binds '{variable}' to forEach node '{target.NodeId}', whose aggregate output is not bindable in specVersion 5.0.");
                        return;
                    }

                    if (targetNode.ForEach != null && !string.Equals(targetNode.ForEach, node.ForEach, StringComparison.Ordinal))
                    {
                        errors.Add($"Node '{node.Id}' binds '{variable}' to '{target.NodeId}' across collections ('{node.ForEach}' vs '{targetNode.ForEach}'), which is not supported in specVersion 5.0.");
                        return;
                    }

                    edges.TryAdd(node.Id, new HashSet<string>(StringComparer.Ordinal));
                    edges[node.Id].Add(target.NodeId);

                    var rendersConcepts = binding.As != null || conceptListNodeIds.Contains(target.NodeId);
                    if (binding.As != null && !WorkflowConceptRenderers.All.Contains(binding.As))
                    {
                        errors.Add($"Node '{node.Id}' binding '{variable}' uses unknown renderer '{binding.As}' (expected 'concept-bullets' or 'concept-context').");
                    }
                    else if (rendersConcepts && !conceptListNodeIds.Contains(target.NodeId))
                    {
                        errors.Add($"Node '{node.Id}' binding '{variable}' renders node '{target.NodeId}' with '{binding.As}' but '{target.NodeId}' declares no concept-list output.");
                    }

                    return;
                default:
                    if (binding.As != null)
                    {
                        errors.Add($"Node '{node.Id}' binding '{variable}' declares renderer '{binding.As}' on non-node source '{binding.From}'.");
                    }

                    return;
            }
        }

        foreach (var node in nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Label))
            {
                errors.Add($"Node '{node.Id}' has no label.");
            }

            if (node.Kind != null)
            {
                errors.Add($"Node '{node.Id}' declares kind '{node.Kind}'; specVersion 5 has one node kind.");
            }

            if (node.Over != null)
            {
                errors.Add($"Node '{node.Id}' declares over, which is removed in specVersion 5 (use forEach).");
            }

            if (node.Steps is { Count: > 0 })
            {
                errors.Add($"Node '{node.Id}' declares steps, which are removed in specVersion 5 (steps are ordinary forEach nodes).");
            }

            if (node.ForEach != null && !TryResolveForEachCollection(node, data, out var forEachError, out _))
            {
                errors.Add($"Node '{node.Id}' {forEachError}");
            }

            if (node.Prompt is null)
            {
                errors.Add($"Node '{node.Id}' declares no prompt.");
            }
            else
            {
                var bindings = node.Bindings ?? new Dictionary<string, WorkflowBindingValue>();

                if (!promptsById.TryGetValue(node.Prompt, out var prompt))
                {
                    errors.Add($"Node '{node.Id}' references undeclared prompt '{node.Prompt}'.");
                }
                else
                {
                    promptReferenceCounts[node.Prompt] = promptReferenceCounts.GetValueOrDefault(node.Prompt) + 1;

                    var declared = new HashSet<string>(prompt.Variables, StringComparer.Ordinal);
                    if (!declared.SetEquals(bindings.Keys))
                    {
                        errors.Add(
                            $"Node '{node.Id}' bindings [{string.Join(", ", bindings.Keys.Order(StringComparer.Ordinal))}] " +
                            $"must exactly match prompt '{node.Prompt}' variables [{string.Join(", ", prompt.Variables.Order(StringComparer.Ordinal))}].");
                    }
                }

                foreach (var (variable, binding) in bindings)
                {
                    CheckBinding(node, variable, binding);
                }
            }

            ValidateNodeOutput(node, manifest, files, catalogSchemas, errors);
        }

        foreach (var orphan in promptsById.Keys.Where(id => promptReferenceCounts.GetValueOrDefault(id) == 0))
        {
            errors.Add($"Prompt '{orphan}' is not referenced by any node.");
        }

        foreach (var overused in promptReferenceCounts.Where(pair => pair.Value > 1).Select(pair => pair.Key))
        {
            errors.Add($"Prompt '{overused}' is referenced by more than one node.");
        }

        ValidateResult(manifest, nodesById, errors);
        ValidateAcyclic(nodes, edges, errors);
    }

    private static bool TryResolveForEachCollection(
        WorkflowNodeSpec node,
        WorkflowPackageData data,
        out string? error,
        out WorkflowDataCollection? collection)
    {
        error = null;
        collection = null;

        if (!WorkflowNodeBindingSources.TryParse(node.ForEach!, out var source, out _)
            || source is not WorkflowNodeBindingSource.Data dataSource)
        {
            error = $"forEach '{node.ForEach}' must be a data: collection reference.";
            return false;
        }

        if (data.Collections.TryGetValue(dataSource.Id, out var resolved))
        {
            collection = resolved;
            return true;
        }

        error = data.Scalars.ContainsKey(dataSource.Id)
            ? $"forEach references scalar data entry '{dataSource.Id}' (forEach requires a collection)."
            : $"forEach references unknown data entry '{dataSource.Id}'.";
        return false;
    }

    private static void ValidateResult(
        WorkflowPackageManifest manifest,
        IReadOnlyDictionary<string, WorkflowNodeSpec> nodesById,
        List<string> errors)
    {
        if (manifest.Result is null)
        {
            errors.Add("result is required in specVersion 5.");
            return;
        }

        if (!manifest.Result.StartsWith(WorkflowNodeBindingSources.NodePrefix, StringComparison.Ordinal)
            || manifest.Result.Length == WorkflowNodeBindingSources.NodePrefix.Length)
        {
            errors.Add($"result '{manifest.Result}' must be 'node:<id>'.");
            return;
        }

        var resultNodeId = manifest.Result[WorkflowNodeBindingSources.NodePrefix.Length..];

        if (!nodesById.TryGetValue(resultNodeId, out var resultNode))
        {
            errors.Add($"result references unknown node '{resultNodeId}'.");
            return;
        }

        if (resultNode.ForEach is null)
        {
            errors.Add($"result must reference a forEach node ('{resultNodeId}' runs once).");
        }
    }

    private static void ValidateNodes(
        WorkflowPackageManifest manifest,
        IReadOnlyDictionary<string, string> files,
        IReadOnlyDictionary<string, string> catalogSchemas,
        List<string> errors)
    {
        var nodes = manifest.Nodes ?? new List<WorkflowNodeSpec>();
        if (nodes.Count == 0)
        {
            errors.Add("nodes is required and must not be empty in specVersion 4.");
            return;
        }

        var promptsById = (manifest.Prompts ?? new List<WorkflowPromptSpec>())
            .GroupBy(p => p.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var nodesById = new Dictionary<string, WorkflowNodeSpec>(StringComparer.Ordinal);
        var promptReferenceCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var edges = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var node in nodes)
        {
            if (!nodesById.TryAdd(node.Id, node))
            {
                errors.Add($"Duplicate node id '{node.Id}'.");
            }
        }

        var conceptListNodeIds = nodes
            .Where(n => n.Output != null)
            .Select(n => n.Id)
            .ToHashSet(StringComparer.Ordinal);
        var mapNodes = nodes.Where(n => string.Equals(n.Kind, WorkflowNodeKinds.Map, StringComparison.Ordinal)).ToList();

        if (mapNodes.Count != 1)
        {
            errors.Add($"specVersion 4.0 requires exactly one map node (found {mapNodes.Count}).");
        }

        void CheckBinding(string nodeId, string variable, WorkflowBindingValue binding, bool inMapStep, bool isFirstMapStep)
        {
            if (!WorkflowNodeBindingSources.TryParse(binding.From, out var source, out var parseError))
            {
                errors.Add($"Node '{nodeId}' binds '{variable}' to {parseError}.");
                return;
            }

            switch (source)
            {
                case WorkflowNodeBindingSource.Input { Name: "sections" }:
                    errors.Add($"Node '{nodeId}' binds '{variable}' to 'input:sections', which is only valid as a map node's 'over'.");
                    return;
                // The v4 item-field closure lives here (parsing is namespace-syntactic;
                // v5 validates fields against the collection's declaration instead).
                case WorkflowNodeBindingSource.Item item when !WorkflowNodeBindingSources.V4ItemFields.Contains(item.Field):
                    errors.Add($"Node '{nodeId}' binds '{variable}' to unknown item field '{item.Field}' (expected name, standard, or id).");
                    return;
                case WorkflowNodeBindingSource.Item { Field: "id" }:
                    errors.Add($"Node '{nodeId}' binds '{variable}' to 'item:id', which is reserved and not yet bindable in specVersion 4.0.");
                    return;
                case WorkflowNodeBindingSource.Data:
                    errors.Add($"Node '{nodeId}' binds '{variable}' to '{binding.From}': the data: namespace requires specVersion 5.");
                    return;
                case WorkflowNodeBindingSource.Item when !inMapStep:
                case WorkflowNodeBindingSource.PreviousStepOutput when !inMapStep:
                    errors.Add($"Node '{nodeId}' binds '{variable}' to '{binding.From}', which is only valid inside map steps.");
                    return;
                case WorkflowNodeBindingSource.PreviousStepOutput when isFirstMapStep:
                    errors.Add($"Node '{nodeId}' first map step cannot bind '{WorkflowNodeBindingSources.PreviousStepOutput}'.");
                    return;
                case WorkflowNodeBindingSource.NodeOutput target:
                    if (!nodesById.TryGetValue(target.NodeId, out var targetNode))
                    {
                        errors.Add($"Node '{nodeId}' binds '{variable}' to unknown node '{target.NodeId}'.");
                        return;
                    }

                    if (string.Equals(targetNode.Kind, WorkflowNodeKinds.Map, StringComparison.Ordinal))
                    {
                        errors.Add($"Node '{nodeId}' binds '{variable}' to map node '{target.NodeId}', whose aggregate output is not bindable.");
                        return;
                    }

                    if (!inMapStep)
                    {
                        edges.TryAdd(nodeId, new HashSet<string>(StringComparer.Ordinal));
                        edges[nodeId].Add(target.NodeId);
                    }

                    var rendersConcepts = binding.As != null || conceptListNodeIds.Contains(target.NodeId);
                    if (binding.As != null && !WorkflowConceptRenderers.All.Contains(binding.As))
                    {
                        errors.Add($"Node '{nodeId}' binding '{variable}' uses unknown renderer '{binding.As}' (expected 'concept-bullets' or 'concept-context').");
                    }
                    else if (rendersConcepts && !conceptListNodeIds.Contains(target.NodeId))
                    {
                        errors.Add($"Node '{nodeId}' binding '{variable}' renders node '{target.NodeId}' with '{binding.As}' but '{target.NodeId}' declares no concept-list output.");
                    }

                    return;
                default:
                    if (binding.As != null)
                    {
                        errors.Add($"Node '{nodeId}' binding '{variable}' declares renderer '{binding.As}' on non-node source '{binding.From}'.");
                    }

                    return;
            }
        }

        void CheckBindingsMatchPrompt(string ownerId, string promptId, IReadOnlyDictionary<string, WorkflowBindingValue> bindings)
        {
            if (!promptsById.TryGetValue(promptId, out var prompt))
            {
                errors.Add($"Node '{ownerId}' references undeclared prompt '{promptId}'.");
                return;
            }

            promptReferenceCounts[promptId] = promptReferenceCounts.GetValueOrDefault(promptId) + 1;

            var declared = new HashSet<string>(prompt.Variables, StringComparer.Ordinal);
            if (!declared.SetEquals(bindings.Keys))
            {
                errors.Add(
                    $"Node '{ownerId}' bindings [{string.Join(", ", bindings.Keys.Order(StringComparer.Ordinal))}] " +
                    $"must exactly match prompt '{promptId}' variables [{string.Join(", ", prompt.Variables.Order(StringComparer.Ordinal))}].");
            }
        }

        foreach (var node in nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Label))
            {
                errors.Add($"Node '{node.Id}' has no label.");
            }

            switch (node.Kind)
            {
                case WorkflowNodeKinds.Prompt:
                    if (node.Prompt is null)
                    {
                        errors.Add($"Node '{node.Id}' is a prompt node but declares no prompt.");
                        break;
                    }

                    var bindings = node.Bindings ?? new Dictionary<string, WorkflowBindingValue>();
                    CheckBindingsMatchPrompt(node.Id, node.Prompt, bindings);
                    foreach (var (variable, binding) in bindings)
                    {
                        CheckBinding(node.Id, variable, binding, inMapStep: false, isFirstMapStep: false);
                    }

                    ValidateNodeOutput(node, manifest, files, catalogSchemas, errors);
                    break;

                case WorkflowNodeKinds.Map:
                    ValidateMapNode(node, errors, CheckBinding, CheckBindingsMatchPrompt);
                    break;

                default:
                    errors.Add($"Node '{node.Id}' has unknown kind '{node.Kind}' (expected 'prompt' or 'map').");
                    break;
            }
        }

        foreach (var orphan in promptsById.Keys.Where(id => promptReferenceCounts.GetValueOrDefault(id) == 0))
        {
            errors.Add($"Prompt '{orphan}' is not referenced by any node.");
        }

        foreach (var (promptId, count) in promptReferenceCounts.Where(pair => pair.Value > 1))
        {
            errors.Add($"Prompt '{promptId}' is referenced by more than one node.");
        }

        ValidateAcyclic(nodes, edges, errors);
    }

    private static void ValidateMapNode(
        WorkflowNodeSpec node,
        List<string> errors,
        Action<string, string, WorkflowBindingValue, bool, bool> checkBinding,
        Action<string, string, IReadOnlyDictionary<string, WorkflowBindingValue>> checkBindingsMatchPrompt)
    {
        if (!string.Equals(node.Over, WorkflowNodeBindingSources.InputSections, StringComparison.Ordinal))
        {
            errors.Add($"Map node '{node.Id}' 'over' must be 'input:sections' in specVersion 4.0.");
        }

        var steps = node.Steps ?? new List<WorkflowMapStepSpec>();
        if (steps.Count == 0)
        {
            errors.Add($"Map node '{node.Id}' has no steps.");
            return;
        }

        var stepIds = new HashSet<string>(StringComparer.Ordinal);
        var upstreamNodes = new HashSet<string>(StringComparer.Ordinal);

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

            checkBindingsMatchPrompt(node.Id, step.Prompt, step.Bindings);

            foreach (var (variable, binding) in step.Bindings)
            {
                checkBinding(node.Id, variable, binding, true, i == 0);

                if (binding.From.StartsWith(WorkflowNodeBindingSources.NodePrefix, StringComparison.Ordinal))
                {
                    upstreamNodes.Add(binding.From[WorkflowNodeBindingSources.NodePrefix.Length..]);

                    if (!string.Equals(binding.As, WorkflowConceptRenderers.ConceptContext, StringComparison.Ordinal))
                    {
                        errors.Add($"Map steps may bind at most one upstream node, rendered as 'concept-context', in specVersion 4.0.");
                    }
                }
            }
        }

        if (upstreamNodes.Count > 1)
        {
            errors.Add($"Map steps may bind at most one upstream node, rendered as 'concept-context', in specVersion 4.0.");
        }
    }

    private static void ValidateNodeOutput(
        WorkflowNodeSpec node,
        WorkflowPackageManifest manifest,
        IReadOnlyDictionary<string, string> files,
        IReadOnlyDictionary<string, string> catalogSchemas,
        List<string> errors)
    {
        if (node.Output is null)
        {
            return;
        }

        if (manifest.Schemas is null || !manifest.Schemas.TryGetValue(node.Output.Schema, out var schemaPath))
        {
            errors.Add($"Node '{node.Id}' output schema '{node.Output.Schema}' is not declared in schemas.");
            return;
        }

        if (!files.TryGetValue(schemaPath, out var schemaText))
        {
            errors.Add($"Schema '{node.Output.Schema}' file '{schemaPath}' is missing from the package.");
            return;
        }

        JsonNode? schema;
        try
        {
            schema = JsonNode.Parse(schemaText);
        }
        catch (JsonException ex)
        {
            errors.Add($"Schema '{node.Output.Schema}' does not parse as JSON: {ex.Message}");
            return;
        }

        // The closure, catalog-shaped since milestone 5: every declared schema must
        // canonically match some catalog output contract (modulo title/description) —
        // schemas are welded to attested agents, and the catalog names them
        // (output-contract-catalog.md). This subsumes the structured-outputs-subset check.
        var canonicalSchema = CanonicalizeSchema(schema);
        if (!catalogSchemas.Values.Any(catalogSchema => CanonicalizeSchema(JsonNode.Parse(catalogSchema)) == canonicalSchema))
        {
            errors.Add($"Schema '{node.Output.Schema}' must canonically match a catalog output contract (modulo title/description).");
        }

        if (node.Output.FailIfEmpty != null && string.IsNullOrWhiteSpace(node.Output.FailIfEmpty))
        {
            errors.Add($"Node '{node.Id}' failIfEmpty must not be blank.");
        }
    }

    /// <summary>Sorted-key serialization with title/description stripped recursively.</summary>
    internal static string CanonicalizeSchema(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            var parts = obj
                .Where(pair => pair.Key is not ("title" or "description"))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{JsonSerializer.Serialize(pair.Key)}:{CanonicalizeSchema(pair.Value)}");
            return "{" + string.Join(",", parts) + "}";
        }

        if (node is JsonArray array)
        {
            return "[" + string.Join(",", array.Select(CanonicalizeSchema)) + "]";
        }

        return node?.ToJsonString() ?? "null";
    }

    private static void ValidateAcyclic(
        IReadOnlyList<WorkflowNodeSpec> nodes,
        IReadOnlyDictionary<string, HashSet<string>> edges,
        List<string> errors)
    {
        // Kahn's algorithm, seeded in manifest order for deterministic error text.
        // Duplicate ids are already reported; deduplicate so the walk still runs.
        nodes = nodes.DistinctBy(n => n.Id, StringComparer.Ordinal).ToList();
        var remainingDeps = nodes.ToDictionary(
            n => n.Id,
            n => new HashSet<string>(edges.GetValueOrDefault(n.Id) ?? new HashSet<string>(), StringComparer.Ordinal),
            StringComparer.Ordinal);
        var resolved = new HashSet<string>(StringComparer.Ordinal);
        var progressed = true;

        while (progressed)
        {
            progressed = false;
            foreach (var node in nodes)
            {
                if (resolved.Contains(node.Id) || remainingDeps[node.Id].Except(resolved).Any())
                {
                    continue;
                }

                resolved.Add(node.Id);
                progressed = true;
            }
        }

        if (resolved.Count < nodes.Count)
        {
            var cyclic = nodes.Where(n => !resolved.Contains(n.Id)).Select(n => n.Id);
            errors.Add($"nodes contain a cycle involving: {string.Join(", ", cyclic)}.");
        }
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
