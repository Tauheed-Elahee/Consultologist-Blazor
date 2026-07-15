using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Consultologist.Api.Workflow;

public sealed record WorkflowPackageManifest(
    string Name,
    string Version,
    int SpecVersion,
    WorkflowTemplatingSpec? Templating = null,
    Dictionary<string, string>? Preludes = null,
    List<WorkflowPromptSpec>? Prompts = null,
    List<WorkflowSectionStepSpec>? SectionSteps = null,
    Dictionary<string, string>? Schemas = null,
    List<WorkflowNodeSpec>? Nodes = null,
    string? DerivedFrom = null,
    Dictionary<string, string>? Data = null,
    string? Result = null);

public sealed record WorkflowTemplatingSpec(
    string Engine,
    string EngineVersion);

public sealed record WorkflowPromptSpec(
    string Id,
    string File,
    List<string> Variables,
    string? Prelude = null);

/// <summary>
/// One ordered step of the per-section prose pipeline (specVersion 3): renders one
/// prompt template with each declared variable bound to an engine binding source.
/// See docs/customizable-workflow/package-format-v3.md.
/// </summary>
public sealed record WorkflowSectionStepSpec(
    string Prompt,
    string Label,
    Dictionary<string, string> Bindings,
    string? Id = null)
{
    public string StepId => Id ?? Prompt;
}

/// <summary>
/// One node of the workflow DAG. Edges are implicit in the bindings' node:
/// references. specVersion 4 declares Kind (prompt/map) with Over/Steps on the map
/// node; specVersion 5 has one kind (Kind absent) with ForEach as the multiplicity
/// property. See package-format-v4.md and -v5.md.
/// </summary>
public sealed record WorkflowNodeSpec(
    string Id,
    string? Kind,
    string Label,
    string? Prompt = null,
    Dictionary<string, WorkflowBindingValue>? Bindings = null,
    WorkflowNodeOutputSpec? Output = null,
    string? Over = null,
    List<WorkflowMapStepSpec>? Steps = null,
    string? ForEach = null);

public sealed record WorkflowNodeOutputSpec(
    string Schema,
    string? FailIfEmpty = null);

/// <summary>
/// One item of a resolved data collection: the declared fields materialized —
/// per-item file content becomes the "content" field
/// (docs/customizable-workflow/package-format-v5.md).
/// </summary>
public sealed record WorkflowDataItem(
    string Id,
    IReadOnlyDictionary<string, string> Fields);

/// <summary>A resolved data collection: the declared item shape plus the items, in index order.</summary>
public sealed record WorkflowDataCollection(
    IReadOnlyList<string> Fields,
    IReadOnlyList<WorkflowDataItem> Items);

/// <summary>The resolved data table of a specVersion-5 package.</summary>
public sealed record WorkflowPackageData(
    IReadOnlyDictionary<string, string> Scalars,
    IReadOnlyDictionary<string, WorkflowDataCollection> Collections);

/// <summary>The parsed shape of a collection's index.json.</summary>
public sealed record WorkflowDataIndexFile(
    List<string>? Fields,
    List<WorkflowDataIndexItem>? Items);

public sealed record WorkflowDataIndexItem(
    string? Id,
    string? Name,
    string? File);

public sealed record WorkflowMapStepSpec(
    string Prompt,
    string Label,
    Dictionary<string, WorkflowBindingValue> Bindings,
    string? Id = null)
{
    public string StepId => Id ?? Prompt;
}

/// <summary>
/// A binding value in a specVersion-4 manifest: either a plain source string
/// ("input:consult_draft") or an object selecting a renderer
/// ({ "from": "node:x", "as": "concept-context" }).
/// </summary>
[JsonConverter(typeof(WorkflowBindingValueConverter))]
public sealed record WorkflowBindingValue(string From, string? As = null);

public sealed class WorkflowBindingValueConverter : JsonConverter<WorkflowBindingValue>
{
    public override WorkflowBindingValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new WorkflowBindingValue(reader.GetString()!);
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("A binding value must be a source string or a { from, as } object.");
        }

        string? from = null;
        string? renderAs = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var property = reader.GetString();
            reader.Read();

            switch (property?.ToLowerInvariant())
            {
                case "from":
                    from = reader.GetString();
                    break;
                case "as":
                    renderAs = reader.GetString();
                    break;
                default:
                    throw new JsonException($"Unknown binding value property '{property}' (expected 'from' or 'as').");
            }
        }

        return from is null
            ? throw new JsonException("A binding value object requires 'from'.")
            : new WorkflowBindingValue(from, renderAs);
    }

    public override void Write(Utf8JsonWriter writer, WorkflowBindingValue value, JsonSerializerOptions options)
    {
        if (value.As is null)
        {
            writer.WriteStringValue(value.From);
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("from", value.From);
        writer.WriteString("as", value.As);
        writer.WriteEndObject();
    }
}

/// <summary>A prompt template loaded from a specVersion-2+ package, ready to render.</summary>
public sealed record WorkflowPromptTemplate(
    string Id,
    string TemplateText,
    IReadOnlyList<string> Variables,
    string? PreludeText);

public sealed record WorkflowPackage(
    WorkflowPackageManifest Manifest,
    string StandardsMarkdown,
    IReadOnlyDictionary<string, WorkflowPromptTemplate>? Prompts = null,
    IReadOnlyList<WorkflowSectionStepSpec>? SectionSteps = null,
    IReadOnlyList<WorkflowNodeSpec>? Nodes = null,
    IReadOnlyDictionary<string, string>? SchemaContracts = null,
    WorkflowPackageData? Data = null)
{
    public string Ref => $"{Manifest.Name}@{Manifest.Version}";

    public bool HasPrompts => Prompts is { Count: > 0 };
}

public sealed record WorkflowPackageResponse(
    string Name,
    string Version,
    int SpecVersion,
    string StandardsMarkdown);

/// <summary>
/// A package reference of the form "name@vYYYY.MM.N" or "name@latest".
/// </summary>
public sealed record WorkflowPackageRef(string Name, string Version)
{
    public const string LatestVersion = "latest";

    private static readonly Regex NamePattern = new("^[a-z0-9][a-z0-9-]*$", RegexOptions.Compiled);

    public bool IsLatest => string.Equals(Version, LatestVersion, StringComparison.Ordinal);

    public static bool TryParse(string? value, out WorkflowPackageRef? packageRef)
    {
        packageRef = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Trim().Split('@');
        if (parts.Length != 2 || !NamePattern.IsMatch(parts[0]))
        {
            return false;
        }

        var version = parts[1];
        if (!string.Equals(version, LatestVersion, StringComparison.Ordinal)
            && !CalVerVersion.TryParse(version, out _))
        {
            return false;
        }

        packageRef = new WorkflowPackageRef(parts[0], version);
        return true;
    }

    public override string ToString() => $"{Name}@{Version}";
}

/// <summary>
/// Package version in the form "vYYYY.MM.N": zero-padded month, and a within-month
/// release counter starting at 1. Comparison is numeric, never lexicographic —
/// "v2026.07.10" sorts after "v2026.07.2" even though it sorts before it as a string.
/// </summary>
public readonly record struct CalVerVersion(int Year, int Month, int Counter) : IComparable<CalVerVersion>
{
    private static readonly Regex Pattern = new(@"^v(?<year>\d{4})\.(?<month>\d{2})\.(?<counter>[1-9]\d*)$", RegexOptions.Compiled);

    public static bool TryParse(string? value, out CalVerVersion version)
    {
        version = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var match = Pattern.Match(value.Trim());
        if (!match.Success)
        {
            return false;
        }

        var month = int.Parse(match.Groups["month"].Value);
        if (month is < 1 or > 12)
        {
            return false;
        }

        version = new CalVerVersion(
            int.Parse(match.Groups["year"].Value),
            month,
            int.Parse(match.Groups["counter"].Value));
        return true;
    }

    public int CompareTo(CalVerVersion other)
    {
        var byYear = Year.CompareTo(other.Year);
        if (byYear != 0)
        {
            return byYear;
        }

        var byMonth = Month.CompareTo(other.Month);
        return byMonth != 0 ? byMonth : Counter.CompareTo(other.Counter);
    }

    public override string ToString() => $"v{Year:D4}.{Month:D2}.{Counter}";
}
