namespace Consultologist.Api.Tests;

/// <summary>
/// The engine catalog's schema table as validator input, loaded from the real
/// bundled catalog so fixture packages validate against exactly what ships.
/// </summary>
public static class TestOutputContracts
{
    public static readonly IReadOnlyDictionary<string, string> CatalogSchemas = Load();

    /// <summary>The catalog's concept-list schema text (the former engine const).</summary>
    public static string ConceptListSchema => CatalogSchemas["concept-list"];

    private static IReadOnlyDictionary<string, string> Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Consultologist.sln")))
        {
            dir = dir.Parent;
        }

        var catalog = Consultologist.Api.Agents.OutputContractCatalog.Load(Path.Combine(dir!.FullName, "agents"));

        return catalog.Entries.Values
            .Where(entry => entry.SchemaJson != null)
            .ToDictionary(entry => entry.ContractId, entry => entry.SchemaJson!, StringComparer.Ordinal);
    }
}
