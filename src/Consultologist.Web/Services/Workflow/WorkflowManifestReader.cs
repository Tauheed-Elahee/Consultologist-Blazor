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

    /// <summary>One binding: a prompt variable, its source, and the optional concept renderer.</summary>
    public sealed record BindingView(string Variable, string From, string? As);

    public sealed record NodeView(
        string Id,
        string Label,
        string? Prompt,
        string? ForEach,
        IReadOnlyList<BindingView> Bindings,
        bool IsResult,
        bool HasOutput,
        IReadOnlyList<string>? Aggregate = null);

    public sealed record DataItemView(string Id, string Name, string File);

    public sealed record CollectionView(string Id, string Directory, IReadOnlyList<DataItemView> Items);

    public static IReadOnlyList<PromptView> ReadPrompts(JsonElement manifest)
    {
        var prompts = new List<PromptView>();

        if (!TryGetProperty(manifest, "prompts", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return prompts;
        }

        foreach (var prompt in array.EnumerateArray())
        {
            var variables = new List<string>();

            if (TryGetProperty(prompt, "variables", out var vars) && vars.ValueKind == JsonValueKind.Array)
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

        if (!TryGetProperty(manifest, "nodes", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return nodes;
        }

        foreach (var node in array.EnumerateArray())
        {
            var bindings = new List<BindingView>();

            if (TryGetProperty(node, "bindings", out var bindingsElement) && bindingsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var binding in bindingsElement.EnumerateObject())
                {
                    bindings.Add(ReadBinding(binding.Name, binding.Value));
                }
            }

            List<string>? aggregate = null;

            if (TryGetProperty(node, "aggregate", out var aggregateElement) && aggregateElement.ValueKind == JsonValueKind.Array)
            {
                aggregate = aggregateElement.EnumerateArray()
                    .Select(entry => entry.GetString() ?? string.Empty)
                    .Where(entry => entry.Length > 0)
                    .ToList();
            }

            var id = ReadString(node, "id") ?? string.Empty;
            nodes.Add(new NodeView(
                id,
                ReadString(node, "label") ?? id,
                ReadString(node, "prompt"),
                ReadString(node, "forEach"),
                bindings,
                string.Equals(id, resultNodeId, StringComparison.Ordinal),
                TryGetProperty(node, "output", out var output) && output.ValueKind == JsonValueKind.Object,
                aggregate));
        }

        return nodes;
    }

    /// <summary>Scalar data entries: values of the data map that are not directories.</summary>
    public static IReadOnlyList<string> ReadScalars(JsonElement manifest)
    {
        var scalars = new List<string>();

        if (!TryGetProperty(manifest, "data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return scalars;
        }

        foreach (var entry in data.EnumerateObject())
        {
            if (entry.Value.ValueKind == JsonValueKind.String && entry.Value.GetString() is { } value && !value.EndsWith('/'))
            {
                scalars.Add(entry.Name);
            }
        }

        return scalars;
    }

    /// <summary>
    /// The manifest's collections joined with each directory's index.json (from
    /// the files dict) so the editor can render one card per item.
    /// </summary>
    public static IReadOnlyList<CollectionView> ReadCollections(JsonElement manifest, IReadOnlyDictionary<string, string> files)
    {
        var collections = new List<CollectionView>();

        if (!TryGetProperty(manifest, "data", out var data) || data.ValueKind != JsonValueKind.Object)
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

    /// <summary>The fork's parent ref, or null for root packages.</summary>
    public static string? ReadDerivedFrom(JsonElement manifest) => ReadString(manifest, "derivedFrom");

    private static string? ReadResultNodeId(JsonElement manifest)
    {
        var result = ReadString(manifest, "result");
        return result != null && result.StartsWith("node:", StringComparison.Ordinal) ? result["node:".Length..] : result;
    }

    private static BindingView ReadBinding(string variable, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => new BindingView(variable, value.GetString() ?? string.Empty, null),
        JsonValueKind.Object => new BindingView(variable, ReadString(value, "from") ?? string.Empty, ReadString(value, "as")),
        _ => new BindingView(variable, value.ToString(), null)
    };

    /// <summary>
    /// The manifest element's property casing depends on the server's response
    /// serializer (the Functions worker default writes PascalCase; repo manifest
    /// sources are camelCase) — the reader accepts either, like the server's own
    /// case-insensitive parsing does.
    /// </summary>
    private static bool TryGetProperty(JsonElement element, string camelName, out JsonElement value)
    {
        if (element.TryGetProperty(camelName, out value))
        {
            return true;
        }

        var pascalName = char.ToUpperInvariant(camelName[0]) + camelName[1..];
        return element.TryGetProperty(pascalName, out value);
    }

    private static string? ReadString(JsonElement element, string property) =>
        TryGetProperty(element, property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
