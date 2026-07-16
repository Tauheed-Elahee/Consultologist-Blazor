using System.Text.Json;

namespace Consultologist.Web.Services.Workflow;

/// <summary>
/// Read-only views over the opaque manifest JsonElement for the editor's
/// display: which prompt texts and data items are editable, and the node
/// summary. Purely presentational — the manifest itself round-trips to the
/// publish endpoint untouched.
/// </summary>
public static class WorkflowManifestReader
{
    public sealed record PromptView(string Id, string File, IReadOnlyList<string> Variables, string? Prelude);

    public sealed record NodeView(
        string Id,
        string Label,
        string? Prompt,
        string? ForEach,
        IReadOnlyList<KeyValuePair<string, string>> Bindings,
        bool IsResult);

    public sealed record DataItemView(string Id, string Name, string File);

    public sealed record CollectionView(string Id, string Directory, IReadOnlyList<DataItemView> Items);

    public static IReadOnlyList<PromptView> ReadPrompts(JsonElement manifest)
    {
        var prompts = new List<PromptView>();

        if (!manifest.TryGetProperty("prompts", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return prompts;
        }

        foreach (var prompt in array.EnumerateArray())
        {
            var variables = new List<string>();

            if (prompt.TryGetProperty("variables", out var vars) && vars.ValueKind == JsonValueKind.Array)
            {
                variables.AddRange(vars.EnumerateArray().Select(v => v.GetString() ?? string.Empty));
            }

            prompts.Add(new PromptView(
                ReadString(prompt, "id") ?? string.Empty,
                ReadString(prompt, "file") ?? string.Empty,
                variables,
                ReadString(prompt, "prelude")));
        }

        return prompts;
    }

    public static IReadOnlyList<NodeView> ReadNodes(JsonElement manifest)
    {
        var nodes = new List<NodeView>();
        var resultNodeId = ReadResultNodeId(manifest);

        if (!manifest.TryGetProperty("nodes", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return nodes;
        }

        foreach (var node in array.EnumerateArray())
        {
            var bindings = new List<KeyValuePair<string, string>>();

            if (node.TryGetProperty("bindings", out var bindingsElement) && bindingsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var binding in bindingsElement.EnumerateObject())
                {
                    bindings.Add(new KeyValuePair<string, string>(binding.Name, DescribeBinding(binding.Value)));
                }
            }

            var id = ReadString(node, "id") ?? string.Empty;
            nodes.Add(new NodeView(
                id,
                ReadString(node, "label") ?? id,
                ReadString(node, "prompt"),
                ReadString(node, "forEach"),
                bindings,
                string.Equals(id, resultNodeId, StringComparison.Ordinal)));
        }

        return nodes;
    }

    /// <summary>
    /// The manifest's collections joined with each directory's index.json (from
    /// the files dict) so the editor can render one card per item.
    /// </summary>
    public static IReadOnlyList<CollectionView> ReadCollections(JsonElement manifest, IReadOnlyDictionary<string, string> files)
    {
        var collections = new List<CollectionView>();

        if (!manifest.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return collections;
        }

        foreach (var entry in data.EnumerateObject())
        {
            var directory = entry.Value.GetString();

            if (string.IsNullOrWhiteSpace(directory) || !directory.EndsWith('/'))
            {
                continue;
            }

            var items = new List<DataItemView>();

            if (files.TryGetValue(directory + "index.json", out var indexJson))
            {
                try
                {
                    using var index = JsonDocument.Parse(indexJson);

                    if (index.RootElement.TryGetProperty("items", out var itemsElement) && itemsElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in itemsElement.EnumerateArray())
                        {
                            var file = ReadString(item, "file");

                            if (string.IsNullOrWhiteSpace(file))
                            {
                                continue;
                            }

                            var id = ReadString(item, "id") ?? file;
                            items.Add(new DataItemView(id, ReadString(item, "name") ?? id, directory + file));
                        }
                    }
                }
                catch (JsonException)
                {
                    // An unparseable index renders as an empty collection; the
                    // publish validator is the authority on integrity.
                }
            }

            collections.Add(new CollectionView(entry.Name, directory, items));
        }

        return collections;
    }

    private static string? ReadResultNodeId(JsonElement manifest)
    {
        var result = ReadString(manifest, "result");
        return result != null && result.StartsWith("node:", StringComparison.Ordinal) ? result["node:".Length..] : result;
    }

    private static string DescribeBinding(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Object => $"{ReadString(value, "from")} (as {ReadString(value, "as")})",
        _ => value.ToString()
    };

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
