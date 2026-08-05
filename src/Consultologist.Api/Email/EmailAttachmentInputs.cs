using Consultologist.Api.Jobs;

namespace Consultologist.Api.Email;

/// <summary>
/// One inbound attachment, as bytes (#237). Nothing in the email path reads
/// it: the parser is the only thing that knows what a format is, and it runs
/// at job start for both doors (docs/DOCUMENT_INPUT.md § 1).
/// </summary>
public sealed record EmailInputAttachment(string FileName, string ContentType, byte[] Content);

/// <summary>
/// Assigns an email's body and attachments to a package's declared input slots
/// (#210). Pure: the processor does the Graph and Durable work, this decides
/// where things land — and since #237, only that. It routes; it does not read.
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

    /// <summary>
    /// Where the message's parts landed: <c>Inputs</c> is the body — the only
    /// thing here that is already text — and <c>Files</c> the attachments,
    /// keyed by the slot each one fills.
    /// </summary>
    public sealed record Resolution(
        IReadOnlyDictionary<string, string>? Inputs,
        IReadOnlyDictionary<string, EmailInputAttachment>? Files,
        string? RejectReason,
        // #294: characters of body text that had nowhere to go. A named
        // attachment outranks the body for the slot it names, so the body is
        // simply dropped — silently, until now. Counted rather than kept: a
        // discarded body is exactly as likely to be PHI as any other.
        int DiscardedBodyCharacters = 0)
    {
        public static Resolution Rejected(string reason) => new(null, null, reason);
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

        // v5/v6 declare nothing, so there is one implicit slot and a file has
        // nowhere of its own to go. This used to concatenate attachments into
        // the body; #237 refuses instead. The concatenation only worked while
        // email decoded files itself, and "this workflow has no slot for that"
        // is the honest answer rather than a silent merge.
        if (declaredInputIds.Count == 0)
        {
            if (attachments.Count > 0)
            {
                return Resolution.Rejected(
                    "This workflow accepts a single input and cannot take an attachment. "
                    + "Paste the referral into the message instead.");
            }

            return new Resolution(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ConsultDraftInputId] = trimmedBody
                },
                null,
                null);
        }

        var files = new Dictionary<string, EmailInputAttachment>(StringComparer.Ordinal);

        // Stems are matched before the body claims anything: naming a file
        // after a slot is a deliberate act, while a body may be nothing but
        // the signature a mail client appended. A named attachment therefore
        // outranks the body for the slot it names.
        var unmatched = new List<EmailInputAttachment>();

        foreach (var attachment in attachments)
        {
            var stem = Path.GetFileNameWithoutExtension(attachment.FileName);
            var slot = declaredInputIds.FirstOrDefault(id => string.Equals(id, stem, StringComparison.OrdinalIgnoreCase));

            if (slot != null && !files.ContainsKey(slot))
            {
                files[slot] = attachment;
            }
            else if (slot != null)
            {
                // Two attachments naming the same slot — genuinely ambiguous,
                // unlike a body the sender may not have written.
                return Resolution.Rejected($"More than one input was supplied for '{slot}'.");
            }
            else
            {
                unmatched.Add(attachment);
            }
        }

        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);

        var bodyUsed = hasBody
            && declaredInputIds.Contains(ConsultDraftInputId, StringComparer.Ordinal)
            && !files.ContainsKey(ConsultDraftInputId);

        if (bodyUsed)
        {
            inputs[ConsultDraftInputId] = trimmedBody;
        }

        // #294: the body is dropped whenever an attachment already claimed
        // the slot it could have filled. That is correct — a named file
        // outranks a body that may be nothing but a signature — but until now
        // it happened without a trace.
        //
        // A dropped body carrying a cloud-storage link is the one case where
        // it is evidence rather than noise: the sender attached a document
        // through OneDrive, Graph never listed it (#291), and the consult ran
        // without it. That produced a clinically detailed note whose
        // Medications section read "not documented" while reading as complete
        // — the failure #294 records.
        //
        // Everything else dropped here is counted, not refused. A covering
        // note is legitimate and must keep working; the count is what will
        // tell us, with evidence rather than a guess, whether more is needed.
        var discarded = hasBody && !bodyUsed ? trimmedBody.Length : 0;

        if (discarded > 0 && InputContent.HasCloudStorageLink(trimmedBody))
        {
            return Resolution.Rejected(
                "The message points at a file stored in the cloud rather than attaching it. "
                + "We cannot open linked files — please attach the document to the message and re-send.");
        }

        if (unmatched.Count == 0)
        {
            return new Resolution(inputs, files, null, discarded);
        }

        // A slot the body already took is not free: the same slot cannot be
        // supplied as both text and a file, and the job start refuses it.
        var free = declaredInputIds
            .Where(id => !files.ContainsKey(id) && !inputs.ContainsKey(id))
            .ToList();

        if (unmatched.Count > free.Count)
        {
            return Resolution.Rejected(
                "More attachments were supplied than the workflow has inputs for. Name each file after the input it belongs to.");
        }

        // One file only. Two or more is where a swap becomes possible and
        // unconfirmable, so it is refused.
        if (unmatched.Count > 1)
        {
            return Resolution.Rejected(
                "Several attachments could fill several inputs and the order is not something we can confirm back to you. Name each file after the input it belongs to.");
        }

        // A lone attachment fills the FIRST free slot, which is not the same
        // as having only one place to go — there may be several free. It is
        // still the right reading: one document and no name is a referral, and
        // declaration order puts the required slot first (the fax-bridge shape
        // pinned by BlankBody_LetsTheAttachmentBecomeTheDraft).
        //
        // A package declaring an optional input before the required one would
        // send it to the optional slot, but that fails loudly rather than
        // quietly: the required slot stays empty and the job start refuses with
        // "Required input(s) '…' missing." (#232).
        files[free[0]] = unmatched[0];
        return new Resolution(inputs, files, null, discarded);
    }
}
