namespace Consultologist.Api.Agents;

/// <summary>
/// The public-registry redaction for agent definitions (#94): instructions,
/// model, schema, and tool types are published; the tool plumbing —
/// tools[].server_url and project_connection_id — is stripped. The transform is
/// deliberately line-based so the publish script's sed expression and this
/// method are trivially equivalent; startup attestation enforces the published
/// artifact equals Redact(bundled git manifest), so any divergence between the
/// two implementations fails loud in production.
/// </summary>
public static class AgentDefinitionRedaction
{
    private static readonly string[] RedactedFields = { "server_url:", "project_connection_id:" };

    public static string Redact(string yaml) =>
        string.Join(
            "\n",
            yaml.Replace("\r\n", "\n")
                .Split('\n')
                .Where(line => !IsRedactedField(line)));

    private static bool IsRedactedField(string line)
    {
        var trimmed = line.TrimStart();
        return RedactedFields.Any(field => trimmed.StartsWith(field, StringComparison.Ordinal));
    }
}
