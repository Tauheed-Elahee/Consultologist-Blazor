using System.Diagnostics.CodeAnalysis;

// At the API root rather than in a namespace of its own: a namespace named
// Text shadows DocumentFormat.OpenXml.Wordprocessing.Text for every file
// under Consultologist.Api, which the DOCX walk uses by its bare name.
namespace Consultologist.Api;

/// <summary>
/// The one canonicalisation applied to text before it is hashed or compared
/// (#251, docs/DOCUMENT_INPUT.md § 2).
///
/// Shared because the rule having been written out four times is what let
/// two of them drift from it: § 2 states the rule as covering line endings,
/// and a lone <c>\r</c> — a classic Mac ending, and what some PDF viewers
/// put on the clipboard — survived both input-path copies untouched. A
/// referral carrying them would have hashed differently from the identical
/// referral typed by hand, and the record would have called them different
/// input for a reason no reader could see.
///
/// Named for the canonical form rather than for line endings, because
/// trailing whitespace goes too — tabs and spaces, not only newlines.
///
/// Deliberately no wider than that. U+2028, U+0085 and the vertical tab are
/// left alone: none has a documented way of arriving here, and each would be
/// a semantic edit to clinical text rather than a canonicalisation of it.
/// Line structure, hyphenation and hard wrapping survive untouched, for the
/// reasons § 2 gives.
///
/// The agent-definition redaction deliberately does NOT use this, and the
/// reason outlives the C# copy #259 deleted. That transform lives once, in the
/// agents repo's <c>publish-agent-definition.sh</c>, and its contract is
/// byte-equivalence with a <c>sed</c> expression. <c>sed</c>'s line model is
/// <c>\n</c>-only and blind to a lone <c>\r</c>, so canonicalising first would
/// split a bare-CR manifest into many lines and strip the <c>server_url:</c>
/// ones where <c>sed</c> sees a single line and strips nothing — same input,
/// two different published documents.
/// </summary>
internal static class CanonicalText
{
    /// <summary>
    /// CRLF to LF, a lone CR to LF, trailing whitespace off the end.
    ///
    /// Null in, null out. Not cosmetic: this also runs over a request's
    /// <c>ConsultDraft</c>, and collapsing null to an empty string would turn
    /// a job's absent draft into an empty one — <c>{"consultDraft":null}</c>
    /// and <c>{"consultDraft":""}</c> are different canonical JSON, so the v2
    /// draft-only hash would move. No shipped path reaches that today:
    /// validation requires one of draft, inputs or files, and a legacy
    /// package folds inputs into the draft before the hash. So this preserves
    /// an invariant rather than fixing a live defect — the same character as
    /// #251 itself.
    /// </summary>
    [return: NotNullIfNotNull(nameof(text))]
    internal static string? Normalize(string? text) =>
        // Order is load-bearing. A lone-CR pass first would turn every
        // \r\n into \n\n and invent a blank line between every pair.
        text?.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd();
}
