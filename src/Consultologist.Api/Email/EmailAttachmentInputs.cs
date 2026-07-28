namespace Consultologist.Api.Email;

/// <summary>One inbound attachment, already decoded to text.</summary>
public sealed record EmailInputAttachment(string FileName, string Text);

/// <summary>
/// Assigns an email's body and attachments to a package's declared input slots
/// (#210). Pure: the processor does the Graph and Durable work, this decides
/// where things land.
///
/// The sender can never be told where a file went — replies carry no PHI, and a
/// filename can itself be PHI ("Smith_John_referral.pdf"). So a positional
/// assignment is unverifiable by construction: fine when one attachment has one
/// place to go, a silent wrong-data error when two could be swapped. Ambiguity
/// is therefore refused rather than guessed.
/// </summary>
public static class EmailAttachmentInputs
{
    public const string ConsultDraftInputId = "consult_draft";

    public sealed record Resolution(
        IReadOnlyDictionary<string, string>? Inputs,
        string? RejectReason)
    {
        public static Resolution Rejected(string reason) => new(null, reason);
    }

    /// <param name="declaredInputIds">
    /// The package's declared slots in declaration order. Empty for v5/v6,
    /// whose only slot is the frozen consult_draft convention.
    /// </param>
    public static Resolution Resolve(
        IReadOnlyList<string> declaredInputIds,
        string? body,
        IReadOnlyList<EmailInputAttachment> attachments)
    {
        var trimmedBody = body?.Trim() ?? string.Empty;
        var hasBody = trimmedBody.Length > 0;

        if (!hasBody && attachments.Count == 0)
        {
            return Resolution.Rejected("The message carried neither a usable body nor an attachment.");
        }

        // v5/v6 declare nothing: one slot, so an attachment appends to the body
        // (or becomes it). Positional has nowhere else to go.
        if (declaredInputIds.Count == 0)
        {
            var parts = new List<string>();

            if (hasBody)
            {
                parts.Add(trimmedBody);
            }

            parts.AddRange(attachments.Select(attachment => attachment.Text.Trim()));

            return new Resolution(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ConsultDraftInputId] = string.Join("\n\n", parts.Where(part => part.Length > 0))
                },
                null);
        }

        var assigned = new Dictionary<string, string>(StringComparer.Ordinal);

        if (hasBody && declaredInputIds.Contains(ConsultDraftInputId, StringComparer.Ordinal))
        {
            assigned[ConsultDraftInputId] = trimmedBody;
        }

        // A filename stem naming a declared slot is the explicit, order-free
        // way to aim an attachment — it wins over position.
        var unmatched = new List<EmailInputAttachment>();

        foreach (var attachment in attachments)
        {
            var stem = Path.GetFileNameWithoutExtension(attachment.FileName);
            var slot = declaredInputIds.FirstOrDefault(id => string.Equals(id, stem, StringComparison.OrdinalIgnoreCase));

            if (slot != null && !assigned.ContainsKey(slot))
            {
                assigned[slot] = attachment.Text.Trim();
            }
            else if (slot != null)
            {
                return Resolution.Rejected($"More than one input was supplied for '{slot}'.");
            }
            else
            {
                unmatched.Add(attachment);
            }
        }

        if (unmatched.Count == 0)
        {
            return new Resolution(assigned, null);
        }

        var free = declaredInputIds.Where(id => !assigned.ContainsKey(id)).ToList();

        if (unmatched.Count > free.Count)
        {
            return Resolution.Rejected(
                "More attachments were supplied than the workflow has inputs for. Name each file after the input it belongs to.");
        }

        // The unambiguous case only: one file, one place it can go.
        if (unmatched.Count > 1)
        {
            return Resolution.Rejected(
                "Several attachments could fill several inputs and the order is not something we can confirm back to you. Name each file after the input it belongs to.");
        }

        assigned[free[0]] = unmatched[0].Text.Trim();
        return new Resolution(assigned, null);
    }
}
