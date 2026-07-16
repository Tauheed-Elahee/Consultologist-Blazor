namespace Consultologist.Api.Workflow;

/// <summary>One section of the consult note: an item of the standards collection.</summary>
public sealed record WorkflowStandardsSection(string Id, string Name, string Content);

/// <summary>
/// The single section source for a resolved package: the result node's forEach
/// collection (sections are package data; package-format-v5.md). Shared by the
/// WorkflowPackages/Current endpoint and consult job start.
/// </summary>
public static class WorkflowPackageSections
{
    public static IReadOnlyList<WorkflowStandardsSection> Resolve(WorkflowPackage package)
    {
        return ResolveCollection(package).Items
            .Select(item => new WorkflowStandardsSection(
                item.Id,
                item.Fields.GetValueOrDefault("name", item.Id),
                item.Fields.GetValueOrDefault("content", string.Empty)))
            .ToList();
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
