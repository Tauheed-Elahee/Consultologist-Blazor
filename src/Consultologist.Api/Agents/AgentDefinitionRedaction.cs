namespace Consultologist.Api.Agents;

/// <summary>
/// The public-registry redaction for agent definitions (#94): instructions,
/// model, schema, and tool types are published; the tool plumbing —
/// tools[].server_url and project_connection_id — is stripped. The transform is
/// deliberately line-based so the publish script's sed expression and this
/// method are trivially equivalent.
///
/// What enforces that equivalence, accurately (#259): the tests below, over
/// two checked-in manifests. Nothing at runtime. This summary used to say
/// startup attestation compares the published artifact against
/// Redact(bundled git manifest) and that any divergence fails loud in
/// production — no code path does that, and this class has no production
/// caller at all. AgentAttestationService compares the registry's published
/// definition against the deployed Foundry agent, which never needs a
/// redaction.
///
/// The leak itself is guarded, and elsewhere: publish-agent-definition.sh
/// greps its own output for server_url and project_connection_id and fails
/// the publish if either survives. What is unguarded is the stronger
/// property — that what the registry holds is a faithful redaction of git,
/// which is what makes the registry the git channel (#16) rather than a
/// store of something plausible. #259 decides whether to wire that check or
/// to delete this class as residue.
/// </summary>
public static class AgentDefinitionRedaction
{
    private static readonly string[] RedactedFields = { "server_url:", "project_connection_id:" };

    /// <summary>
    /// Deliberately not <see cref="CanonicalText.Normalize"/>, though the
    /// CRLF handling looks identical (#251). This transform's contract is
    /// equivalence with the publish script's sed expression in another
    /// repository, not canonicalisation.
    ///
    /// Concretely: <c>sed</c>'s line model is <c>\n</c>-only and blind to a
    /// lone <c>\r</c>. Canonicalising one to <c>\n</c> here would make this
    /// method split a bare-CR manifest into many lines and strip the
    /// <c>server_url:</c> ones, where <c>sed</c> would see a single line and
    /// strip nothing — same input, two different documents. Matching
    /// <c>sed</c> means adopting its line model, <c>\r</c>-blindness
    /// included. If line endings ever need widening here, both sides move
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
