using System.IO.Compression;
using System.Reflection;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Consultologist.Api.Documents;

/// <summary>
/// Reads the text of a Word document (#240). The parser's second registered
/// extractor, and the test of whether adding one costs only that — nothing
/// outside this folder learned that .docx exists.
/// </summary>
internal static class DocxDocumentExtractor
{
    /// <summary>
    /// Every OPC package is a zip, so the signature only says "might be
    /// mine". Deciding properly needs the package opened, which
    /// <see cref="Extract"/> does — keeping this cheap means one open rather
    /// than two, and lets a broken package report as corrupt instead of
    /// falling through to the text branch and reporting the wrong thing.
    /// </summary>
    private static readonly byte[] ZipHeader = [0x50, 0x4B, 0x03, 0x04];

    /// <summary>
    /// The content type of a WordprocessingML main part. Measured, not
    /// assumed: opening an .xlsx as a WordprocessingDocument succeeds and
    /// even yields a MainDocumentPart, and touching its Document throws
    /// InvalidDataException — so "has a main part" is not the discriminator
    /// and the content type is.
    /// </summary>
    private const string WordprocessingMainContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";

    /// <summary>
    /// The per-part XML character bound. Generous relative to the 256 KB text
    /// cap because it counts markup too — this stops an absurd part, it does
    /// not second-guess the output limit.
    /// </summary>
    private const int MaxXmlCharactersPerPart = 16 * 1024 * 1024;

    internal static string ExtractorId { get; } = "openxml/" + (typeof(WordprocessingDocument).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(WordprocessingDocument).Assembly.GetName().Version?.ToString()
        ?? "unknown");

    internal static bool Matches(byte[] bytes) =>
        bytes.Length >= ZipHeader.Length && bytes.AsSpan(0, ZipHeader.Length).SequenceEqual(ZipHeader);

    internal static DocumentExtractionResult Extract(byte[] bytes)
    {
        try
        {
            // Bounded before the SDK sees it. A 10 MB archive can expand to
            // gigabytes, and the byte cap only bounds what arrived — so
            // shipping this without a bound would ship the vector and defer
            // the mitigation. Entry lengths come from the central directory,
            // which costs no decompression to read.
            if (!WithinExpansionBounds(bytes))
            {
                return DocumentExtractionResult.Refused(DocumentExtractionOutcomes.ExpandsTooLarge);
            }

            using var stream = new MemoryStream(bytes, writable: false);

            // The three-argument overload, deliberately: OpenSettings defaults
            // MaxCharactersInPart to 0, meaning unbounded, and the two-argument
            // Open passes exactly that default. The SDK's own remarks call this
            // the mitigation for "an attacker submits a package with an
            // extremely large Open XML part", so leaving it at the default
            // declines the one bound the library does offer.
            //
            // Sized against XML rather than text: WordprocessingML markup runs
            // many times the length of the prose it carries, so a bound equal
            // to the character cap would reject ordinary documents.
            using var document = WordprocessingDocument.Open(stream, false, new OpenSettings
            {
                MaxCharactersInPart = MaxXmlCharactersPerPart
            });

            var main = document.MainDocumentPart;

            if (main == null || !string.Equals(main.ContentType, WordprocessingMainContentType, StringComparison.Ordinal))
            {
                // A valid package that is not a Word document — a spreadsheet,
                // a presentation, or a plain zip. Unsupported rather than
                // corrupt: there is nothing wrong with the file.
                return DocumentExtractionResult.Refused(DocumentExtractionOutcomes.UnsupportedType);
            }

            var text = new StringBuilder();

            // Headers first, then the body, then footers. Including them is a
            // deliberate choice: a referral's date and clinic often live only
            // in the letterhead, and dropping content the sender put in the
            // document would be the silent loss this project refuses
            // elsewhere. A .docx stores them once rather than per page, so
            // the cost is one letterhead and not one per page.
            foreach (var header in main.HeaderParts)
            {
                AppendBlocks(header.Header, text);
            }

            AppendBlocks(main.Document.Body, text);

            foreach (var footer in main.FooterParts)
            {
                AppendBlocks(footer.Footer, text);
            }

            if (text.Length > DocumentExtraction.MaxCharacters)
            {
                return DocumentExtractionResult.Refused(DocumentExtractionOutcomes.TooMuchText);
            }

            // A revision layer existed and was resolved one way. Worth saying
            // so: it is the difference between a clean document and one where
            // something was dropped to produce this text.
            var hadRevisions = main.Document.Body?.Descendants<DeletedRun>().Any() == true
                || main.Document.Body?.Descendants<InsertedRun>().Any() == true;

            return text.Length == 0
                ? DocumentExtractionResult.Refused(DocumentExtractionOutcomes.Empty)
                : DocumentExtractionResult.Extracted(text.ToString(), ExtractorId, null, hadRevisions);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // FileFormatException for a truncated or non-zip payload,
            // InvalidDataException for a package whose parts do not parse.
            // Broad for the same reason the PDF path is: hostile input
            // produces whatever it produces.
            return DocumentExtractionResult.Refused(DocumentExtractionOutcomes.Corrupt);
        }
    }

    private static bool WithinExpansionBounds(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        if (archive.Entries.Count > DocumentExtraction.MaxArchiveEntries)
        {
            return false;
        }

        long declared = 0;

        foreach (var entry in archive.Entries)
        {
            declared += entry.Length;

            if (declared > DocumentExtraction.MaxExpandedBytes)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Paragraphs and tables, in document order. Structure is preserved
    /// because losing it changes meaning: <c>InnerText</c> concatenates the
    /// whole body with no separators at all, which turns two paragraphs into
    /// one sentence and a medication table into "Amlodipine5 mgRamipril10 mg".
    /// That is the same failure the milestone refused when it declined to
    /// rejoin hard-wrapped PDF lines.
    /// </summary>
    private static void AppendBlocks(OpenXmlElement? container, StringBuilder into)
    {
        if (container == null)
        {
            return;
        }

        foreach (var element in container.ChildElements)
        {
            switch (element)
            {
                case Paragraph paragraph:
                    into.Append(RunText(paragraph));
                    into.Append('\n');
                    break;

                case Table table:
                    foreach (var row in table.Elements<TableRow>())
                    {
                        into.AppendJoin('\t', row.Elements<TableCell>().Select(RunText));
                        into.Append('\n');
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// The accepted view of a block: what the sender sees on their screen.
    ///
    /// Deleted runs are dropped and inserted runs kept, which matters more
    /// than it reads. A naive walk of a document whose author changed a dose
    /// from 5 mg to 10 mg yields "Dose is 10 mg5 mg daily." — both values,
    /// adjacent, in clinical text. Hidden runs go too: text marked vanish is
    /// not text the sender is showing anyone.
    /// </summary>
    private static string RunText(OpenXmlElement block)
    {
        var text = new StringBuilder();

        foreach (var run in block.Descendants<Run>())
        {
            if (run.Ancestors<DeletedRun>().Any() || run.RunProperties?.Vanish != null)
            {
                continue;
            }

            foreach (var child in run.ChildElements)
            {
                switch (child)
                {
                    // Text only. Never DeletedText — a second guard in case a
                    // deleted run reaches here without its w:del wrapper — and
                    // never FieldCode, which would otherwise interleave raw
                    // instructions like MERGEFIELD Name into the prose.
                    case Text value:
                        text.Append(value.Text);
                        break;

                    case TabChar:
                        text.Append('\t');
                        break;

                    case Break:
                        text.Append('\n');
                        break;
                }
            }
        }

        return text.ToString();
    }
}
