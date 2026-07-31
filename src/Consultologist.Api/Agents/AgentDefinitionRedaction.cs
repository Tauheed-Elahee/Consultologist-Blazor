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

    /// <summary>
    /// Deliberately not <see cref="CanonicalText.Normalize"/>, though the
    /// CRLF handling looks identical (#251). This transform's contract is
    /// equivalence with the publish script's sed expression in another
    /// repository, not canonicalisation — and startup attestation enforces
    /// published == Redact(bundled manifest), so teaching this side about a
    /// lone <c>\r</c> without teaching the sed would fail loud in
    /// production. If line endings ever need widening here, both sides move
    /// together or neither does.
    /// </summary>
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
