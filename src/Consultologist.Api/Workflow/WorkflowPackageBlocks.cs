namespace Consultologist.Api.Workflow;

/// <summary>One deliverable block: in v5 a section (standards item), in v6 a result-aggregator expansion entry.</summary>
public sealed record WorkflowDeliverableBlock(string Id, string Name, string Content);

/// <summary>
/// The single block source for a resolved package: v5 = the result node's
/// forEach collection; v6 = the result aggregator's expansion. Shared by the
/// WorkflowPackages/Current endpoint and consult job start.
/// </summary>
public static class WorkflowPackageBlocks
{
    public static IReadOnlyList<WorkflowDeliverableBlock> Resolve(WorkflowPackage package)
    {
        if (package.Manifest.SpecVersion == 6)
        {
            return ResolveBlocks(package);
        }

        return ResolveCollection(package).Items
            .Select(item => new WorkflowDeliverableBlock(
                item.Id,
                item.Fields.GetValueOrDefault("name", item.Id),
                item.Fields.GetValueOrDefault("content", string.Empty)))
            .ToList();
    }

    /// <summary>
    /// v6: the deliverable's blocks — the result aggregator's expansion in source
    /// order (package-format-v6-design.md § 4). forEach sources contribute one
    /// block per item (composite "nodeId:itemId" ids, collection index order);
    /// scalar sources one block under the node id.
    /// </summary>
    public static IReadOnlyList<WorkflowDeliverableBlock> ResolveBlocks(WorkflowPackage package)
    {
        var nodes = package.Nodes ?? new List<WorkflowNodeSpec>();
        var nodesById = nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var resultNode = nodesById.GetValueOrDefault(package.ResultNodeId ?? string.Empty)
            ?? throw new InvalidOperationException($"Package {package.Ref} has no result node '{package.ResultNodeId}'.");

        if (resultNode.Aggregate is null)
        {
            throw new InvalidOperationException($"Package {package.Ref} result node '{resultNode.Id}' is not an aggregator (specVersion 6 requires one).");
        }

        var blocks = new List<WorkflowDeliverableBlock>();

        foreach (var sourceRef in resultNode.Aggregate)
        {
            var sourceId = sourceRef.StartsWith(WorkflowNodeBindingSources.NodePrefix, StringComparison.Ordinal)
                ? sourceRef[WorkflowNodeBindingSources.NodePrefix.Length..]
                : sourceRef;
            var source = nodesById.GetValueOrDefault(sourceId)
                ?? throw new InvalidOperationException($"Package {package.Ref} result aggregator references unknown node '{sourceId}'.");

            if (source.ForEach != null)
            {
                var collectionId = source.ForEach[WorkflowNodeBindingSources.DataPrefix.Length..];
                var collection = package.Data?.Collections.GetValueOrDefault(collectionId)
                    ?? throw new InvalidOperationException($"Package {package.Ref} has no data collection '{collectionId}'.");

                blocks.AddRange(collection.Items.Select(item => new WorkflowDeliverableBlock(
                    $"{sourceId}:{item.Id}",
                    item.Fields.GetValueOrDefault("name", item.Id),
                    item.Fields.GetValueOrDefault("content", string.Empty))));
            }
            else
            {
                blocks.Add(new WorkflowDeliverableBlock(sourceId, source.Label, string.Empty));
            }
        }

        return blocks;
    }

    /// <summary>The result node's forEach collection — the items a v5 job fans over.</summary>
    public static WorkflowDataCollection ResolveCollection(WorkflowPackage package)
    {
        var resultNode = package.Nodes?.FirstOrDefault(node => node.Id == package.ResultNodeId)
            ?? throw new InvalidOperationException($"Package {package.Ref} has no result node '{package.ResultNodeId}'.");
        var collectionId = resultNode.ForEach?[WorkflowNodeBindingSources.DataPrefix.Length..]
            ?? throw new InvalidOperationException($"Package {package.Ref} result node '{resultNode.Id}' declares no forEach.");

        return package.Data?.Collections.GetValueOrDefault(collectionId)
            ?? throw new InvalidOperationException($"Package {package.Ref} has no data collection '{collectionId}'.");
    }
}
