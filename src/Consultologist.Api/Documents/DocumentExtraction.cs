namespace Consultologist.Api.Documents;

/// <summary>
/// What became of an attempt to read a document (#235). Dispositions, so
/// kebab-case values — the same shape and convention as
/// <see cref="Email.EmailIntakeOutcomes"/>. Persisted entity statuses are
/// PascalCase in this codebase; these are neither persisted nor statuses.
/// </summary>
public static class DocumentExtractionOutcomes
{
    public const string Extracted = "extracted";
    public const string UnsupportedType = "unsupported-type";
    public const string Corrupt = "corrupt";
    public const string PasswordProtected = "password-protected";
    // A PDF with pages but no glyphs: a scan or a fax. Its own outcome
    // because the copy differs, and because it is the case #188's fax
    // parity is blocked on until OCR (#239).
    public const string NoTextLayer = "no-text-layer";
    public const string Empty = "empty";
    public const string TooLarge = "too-large";
    public const string TooManyPages = "too-many-pages";
    // #240: a container whose declared contents dwarf the file itself. Its own
    // outcome because the file was within every byte bound and still cannot be
    // read safely.
    public const string ExpandsTooLarge = "expands-too-large";
    // Never truncation: half a referral generating a whole consult is a
    // clinical wrong-data error (docs/DOCUMENT_INPUT.md § 4).
    public const string TooMuchText = "too-much-text";
    // #235: the parse outran its wall clock. See ExtractAsync for what this
    // does and does not bound.
    public const string TimedOut = "timed-out";
}

internal sealed record DocumentExtractionResult(
    string Outcome,
    string? Text,
    string? ExtractorId,
    int? PageCount,
    // #240: the document carried tracked changes and this is the accepted
    // view of them. Recorded because taking that view drops content that was
    // in the file — correct, since the author deleted it, but still a drop,
    // and this project makes its drops visible.
    bool TrackedChangesResolved = false)
{
    internal static DocumentExtractionResult Refused(string outcome) => new(outcome, null, null, null);

    internal static DocumentExtractionResult Extracted(
        string text,
        string extractorId,
        int? pageCount,
        bool trackedChangesResolved = false) =>
        new(DocumentExtractionOutcomes.Extracted, text, extractorId, pageCount, trackedChangesResolved);
}

/// <summary>
/// The sole authority on document formats (#234, docs/DOCUMENT_INPUT.md
/// § 1-§ 2). Nothing outside this folder knows what a PDF is: the two
/// sources — the Consults upload and email intake — hand over bytes and
/// render or map whatever named outcome comes back.
///
/// Dispatch is on content, never on a filename. A source-side extension
/// gate would be a second authority that can disagree with this one, and a
/// .txt full of PDF bytes is the case that exposes it.
///
/// Pure: no I/O, no configuration, no logging. Adding a format means one
/// entry in <see cref="Formats"/> plus its extractor, and nothing else
/// anywhere — which is #240's acceptance criterion.
///
/// The one dependency outside this folder is
/// <see cref="CanonicalText.Normalize"/>, and it is not a format concern: it
/// is the canonicalisation the job starter applies to every input, shared
/// here so a document and the same text typed cannot differ on line endings
/// alone (#251). Formats stay sealed in; text canonicalisation was never in.
/// </summary>
internal static class DocumentExtraction
{
    // Bytes accepted. Bounds what arrives, and only that: PdfPig retains
    // every object it resolves for a document's lifetime, so peak memory is
    // a multiple of this rather than a fraction (docs/DOCUMENT_INPUT.md § 4).
    internal const int MaxBytes = 10 * 1024 * 1024;

    // Parse cost is per page, not per byte — a small file can declare
    // thousands of pages.
    internal const int MaxPages = 100;

    // Characters produced, mirroring ConsultGenerationJobs.MaxInputLength:
    // text that clears this bound is text the API would accept as an input.
    internal const int MaxCharacters = 256 * 1024;

    // #240: a container's cost is not its size. Read from the central
    // directory, which costs no decompression — a hostile archive can lie
    // there, so this is a first bound and not the only one (#241).
    internal const long MaxExpandedBytes = 100L * 1024 * 1024;
    internal const int MaxArchiveEntries = 512;

    internal static readonly TimeSpan MaxParseDuration = TimeSpan.FromSeconds(20);

    private sealed record DocumentFormat(Func<byte[], bool> Matches, Func<byte[], DocumentExtractionResult> Extract);

    /// <summary>
    /// Signature-matched formats, tried in order. Text is not here because
    /// it has no signature — it is the fallback in <see cref="Extract"/>,
    /// which is the honest way to say "everything else, if it reads as
    /// text".
    /// </summary>
    private static readonly IReadOnlyList<DocumentFormat> Formats =
    [
        new DocumentFormat(PdfDocumentExtractor.Matches, PdfDocumentExtractor.Extract),
        new DocumentFormat(DocxDocumentExtractor.Matches, DocxDocumentExtractor.Extract)
    ];

    /// <summary>
    /// Reads a document, bounded by the wall clock.
    /// </summary>
    /// <remarks>
    /// Honest about what this bounds: PdfPig is synchronous and accepts no
    /// CancellationToken, so the timeout releases the *caller* and does not
    /// stop the *work* — an orphaned thread keeps running until it finishes
    /// or the worker recycles. It is still worth having, because the
    /// alternative is a request that never returns, and a hang would
    /// otherwise be the one failure with no named outcome. Bounding the
    /// work itself needs process isolation, which #241 weighs.
    ///
    /// The timeout wraps the whole parse rather than a page loop: PdfPig
    /// resolves the header, the cross-reference table — including a
    /// brute-force rescan of the entire file when that table is damaged —
    /// and the page tree inside Open, which is where its historical hangs
    /// lived.
    /// </remarks>
    internal static async Task<DocumentExtractionResult> ExtractAsync(
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var parse = Task.Run(() => Extract(bytes), cancellationToken);

        try
        {
            return await parse.WaitAsync(MaxParseDuration, cancellationToken);
        }
        catch (TimeoutException)
        {
            return DocumentExtractionResult.Refused(DocumentExtractionOutcomes.TimedOut);
        }
    }

    internal static DocumentExtractionResult Extract(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return DocumentExtractionResult.Refused(DocumentExtractionOutcomes.Empty);
        }

        if (bytes.Length > MaxBytes)
        {
            return DocumentExtractionResult.Refused(DocumentExtractionOutcomes.TooLarge);
        }

        foreach (var format in Formats)
        {
            if (format.Matches(bytes))
            {
                return Normalize(format.Extract(bytes));
            }
        }

        if (!TextDocumentDecoder.LooksLikeText(bytes))
        {
            return DocumentExtractionResult.Refused(DocumentExtractionOutcomes.UnsupportedType);
        }

        var text = TextDocumentDecoder.Decode(bytes);

        if (text.Length > MaxCharacters)
        {
            return DocumentExtractionResult.Refused(DocumentExtractionOutcomes.TooMuchText);
        }

        return Normalize(DocumentExtractionResult.Extracted(text, TextDocumentDecoder.ExtractorId, null));
    }

    /// <summary>
    /// Conservative and nothing more: line endings to LF, trailing
    /// whitespace off the end — <see cref="LineEndings.Normalize"/>, the
    /// same call the job starter makes over every input so that a document
    /// and the same text typed cannot differ on line endings alone.
    ///
    /// Deliberately not done here (docs/DOCUMENT_INPUT.md § 2): de-hyphenating
    /// words split across a line break, rejoining hard-wrapped lines into
    /// paragraphs, stripping repeated page headers. Each reads better and
    /// each can corrupt clinical text — rejoining lines turns a
    /// one-per-line medication list into a run-on sentence.
    /// </summary>
    private static DocumentExtractionResult Normalize(DocumentExtractionResult result)
    {
        if (result.Text == null)
        {
            return result;
        }

        var text = CanonicalText.Normalize(result.Text);

        // A document of nothing but whitespace is empty, not extracted —
        // otherwise it would fill a required input slot with a blank string
        // and the job would reject it later with a worse message.
        return text.Length == 0
            ? DocumentExtractionResult.Refused(DocumentExtractionOutcomes.Empty)
            : result with { Text = text };
    }

    /// <summary>
    /// True when text came out and there is some of it. Everything else is
    /// a refusal the caller renders or maps.
    /// </summary>
    internal static bool Succeeded(DocumentExtractionResult result) =>
        string.Equals(result.Outcome, DocumentExtractionOutcomes.Extracted, StringComparison.Ordinal);
}
