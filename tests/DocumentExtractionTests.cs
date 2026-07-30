using System.Text;
using Consultologist.Api.Documents;
using Consultologist.Api.Email;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Security;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System.IO.Compression;

namespace Consultologist.Api.Tests;

/// <summary>
/// The parser that is the sole authority on document formats (#235,
/// docs/DOCUMENT_INPUT.md § 1-§ 4). Dispatch is on content rather than on a
/// filename, every failure is a named outcome rather than an exception, and
/// nothing is ever truncated.
///
/// Every fixture is built here rather than checked in: this repository has
/// never committed a binary test file, and PDFsharp — already a dependency —
/// can produce each case the parser needs to distinguish.
/// </summary>
public class DocumentExtractionTests
{
    private const string Referral = "Emily Lee is a 54 year old woman.\nOncotype DX recurrence score 20.";

    static DocumentExtractionTests()
    {
        // PDFsharp's Core build resolves no system fonts on Linux, so writing
        // a PDF with text needs a resolver. The production renderer embeds
        // Liberation Sans for the same reason (#159); borrow it rather than
        // shipping a second copy.
        GlobalFontSettings.FontResolver ??= new EmbeddedLiberationSans();
    }

    // ---- text ----------------------------------------------------------

    [Fact]
    public void Utf8Text_IsExtracted()
    {
        var result = DocumentExtraction.Extract(Encoding.UTF8.GetBytes(Referral));

        Assert.Equal(DocumentExtractionOutcomes.Extracted, result.Outcome);
        Assert.Equal(Referral, result.Text);
        Assert.Equal("text/1", result.ExtractorId);
    }

    [Fact]
    public void Utf8WithBom_DropsTheMarkInsteadOfSmugglingIt()
    {
        // A retained U+FEFF is invisible in every viewer and changes the
        // effective-input hash, so the same file would hash differently
        // through the two doors (#242).
        var bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(Referral)).ToArray();

        var result = DocumentExtraction.Extract(bytes);

        Assert.Equal(Referral, result.Text);
        Assert.DoesNotContain('﻿', result.Text!);
    }

    [Fact]
    public void Utf16Text_IsDecodedFromItsByteOrderMark()
    {
        // Blind UTF-8 decoding turns this into "\0R\0e\0f..." — the defect
        // #242 records.
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Referral)).ToArray();

        var result = DocumentExtraction.Extract(bytes);

        Assert.Equal(DocumentExtractionOutcomes.Extracted, result.Outcome);
        Assert.Equal(Referral, result.Text);
    }

    [Fact]
    public void Windows1252Text_FallsBackRatherThanCorrupting()
    {
        // The characters that matter clinically are the ones Word emits:
        // accented names and an em dash. Blind UTF-8 replaces each with
        // U+FFFD.
        const string prose = "Résumé of prior notes — see attached.";
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var result = DocumentExtraction.Extract(Encoding.GetEncoding(1252).GetBytes(prose));

        Assert.Equal(prose, result.Text);
        Assert.DoesNotContain('�', result.Text!);
    }

    [Fact]
    public void BinaryBytes_AreRefusedAsUnsupported()
    {
        var result = DocumentExtraction.Extract([0x00, 0x01, 0x02, 0x00, 0xFF]);

        Assert.Equal(DocumentExtractionOutcomes.UnsupportedType, result.Outcome);
        Assert.Null(result.Text);
    }

    [Fact]
    public void CarriageReturnsAreNormalisedAway()
    {
        var result = DocumentExtraction.Extract(Encoding.UTF8.GetBytes("One.\r\nTwo.\r\n"));

        Assert.Equal("One.\nTwo.", result.Text);
    }

    [Fact]
    public void WhitespaceOnly_IsEmptyRatherThanExtracted()
    {
        // Otherwise it would fill a required slot with a blank string and be
        // rejected later with a worse message.
        var result = DocumentExtraction.Extract(Encoding.UTF8.GetBytes("   \n\n  \n"));

        Assert.Equal(DocumentExtractionOutcomes.Empty, result.Outcome);
    }

    [Fact]
    public void TextOverTheCharacterCap_IsRefusedNeverTruncated()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('x', DocumentExtraction.MaxCharacters + 1));

        var result = DocumentExtraction.Extract(bytes);

        Assert.Equal(DocumentExtractionOutcomes.TooMuchText, result.Outcome);
        Assert.Null(result.Text);
    }

    [Fact]
    public void BytesOverTheFileCap_AreRefusedBeforeAnyParsing()
    {
        var result = DocumentExtraction.Extract(new byte[DocumentExtraction.MaxBytes + 1]);

        Assert.Equal(DocumentExtractionOutcomes.TooLarge, result.Outcome);
    }

    // ---- pdf -----------------------------------------------------------

    [Fact]
    public void TextPdf_IsExtracted()
    {
        var result = DocumentExtraction.Extract(TextPdf(Referral));

        Assert.Equal(DocumentExtractionOutcomes.Extracted, result.Outcome);
        Assert.Contains("Emily Lee", result.Text);
        Assert.StartsWith("pdfpig/", result.ExtractorId);
        Assert.Equal(1, result.PageCount);
    }

    [Fact]
    public void PdfWhoseHeaderIsNotAtOffsetZero_IsStillRecognised()
    {
        // The specification allows preceding bytes and real scanners emit
        // them. Dispatching on a filename would not have noticed either way.
        var bytes = Encoding.ASCII.GetBytes("junk").Concat(TextPdf(Referral)).ToArray();

        var result = DocumentExtraction.Extract(bytes);

        Assert.Equal(DocumentExtractionOutcomes.Extracted, result.Outcome);
    }

    [Fact]
    public void ContentPicksTheExtractor_AndThereIsNoFilenameToMislead()
    {
        // The invariant the re-cut turns on: a source-side extension gate
        // could disagree with this, which is why both were deleted rather
        // than extended. Extract takes bytes and nothing else.
        Assert.Equal("text/1", DocumentExtraction.Extract(Encoding.UTF8.GetBytes(Referral)).ExtractorId);
        Assert.StartsWith("pdfpig/", DocumentExtraction.Extract(TextPdf(Referral)).ExtractorId);
    }

    [Fact]
    public void UserPasswordPdf_IsRefusedAndNeverGuessedAt()
    {
        // Produced by the app's own delivery renderer, which is exactly the
        // file a user might reply with. It must refuse — trying the account's
        // delivery password here would make intake a decryption oracle
        // (docs/DOCUMENT_INPUT.md § 3).
        var bytes = ConsultDocumentPdf.Render("# Consult\n\nBody.", "correct-horse-battery-16");

        var result = DocumentExtraction.Extract(bytes);

        Assert.Equal(DocumentExtractionOutcomes.PasswordProtected, result.Outcome);
        Assert.Null(result.Text);
    }

    [Fact]
    public void OwnerPasswordOnlyPdf_IsExtracted()
    {
        // Permissions-only encryption, which hospital and EMR systems emit
        // constantly and every viewer opens without prompting. Refusing these
        // would reject a large share of legitimate referrals.
        var result = DocumentExtraction.Extract(TextPdf(Referral, ownerPassword: "owner-secret-1234"));

        Assert.Equal(DocumentExtractionOutcomes.Extracted, result.Outcome);
        Assert.Contains("Emily Lee", result.Text);
    }

    [Fact]
    public void PdfWithPagesButNoText_IsNotReportedAsSuccess()
    {
        var result = DocumentExtraction.Extract(BlankPdf(pages: 2));

        Assert.NotEqual(DocumentExtractionOutcomes.Extracted, result.Outcome);
        Assert.Equal(DocumentExtractionOutcomes.Empty, result.Outcome);
    }

    [Fact]
    public void ImageOnlyPdf_IsNoTextLayerRatherThanEmpty()
    {
        // The scan-or-fax case, and the most likely refusal once PDFs are
        // accepted. It is distinct from a blank page because the copy differs
        // and because this is precisely what #188's fax parity waits on OCR
        // (#239) for.
        var result = DocumentExtraction.Extract(ImageOnlyPdf());

        Assert.Equal(DocumentExtractionOutcomes.NoTextLayer, result.Outcome);
        Assert.Null(result.Text);
    }

    [Fact]
    public void PdfOverThePageCap_IsRefusedBeforeContentIsDecoded()
    {
        var result = DocumentExtraction.Extract(BlankPdf(DocumentExtraction.MaxPages + 1));

        Assert.Equal(DocumentExtractionOutcomes.TooManyPages, result.Outcome);
    }

    [Fact]
    public void TruncatedPdf_IsCorruptRatherThanThrowing()
    {
        // This covers the format-exception path only: truncation was measured
        // across 95 cut points and produced PdfDocumentFormatException every
        // time it failed at all. The catch in PdfDocumentExtractor is
        // deliberately wider than this test proves, because PdfPig's open
        // issues (#1268, #1277) document IndexOutOfRangeException and
        // NullReferenceException escaping from crafted files whose xref
        // offsets go unchecked. Those need the fuzzer's own inputs to
        // reproduce (#241), so do not narrow that catch to match this test.
        var whole = TextPdf(Referral);

        var result = DocumentExtraction.Extract(whole.Take(whole.Length * 2 / 5).ToArray());

        Assert.Equal(DocumentExtractionOutcomes.Corrupt, result.Outcome);
    }

    [Fact]
    public async Task ExtractAsync_ReturnsTheSameResultWhenItBeatsTheClock()
    {
        var result = await DocumentExtraction.ExtractAsync(
            Encoding.UTF8.GetBytes(Referral),
            CancellationToken.None);

        Assert.Equal(DocumentExtractionOutcomes.Extracted, result.Outcome);
        Assert.Equal(Referral, result.Text);
    }

    // ---- docx ----------------------------------------------------------

    [Fact]
    public void Docx_IsExtracted()
    {
        var result = DocumentExtraction.Extract(Docx());

        Assert.Equal(DocumentExtractionOutcomes.Extracted, result.Outcome);
        Assert.Contains("First paragraph.", result.Text);
        Assert.StartsWith("openxml/", result.ExtractorId);
    }

    [Fact]
    public void DocxParagraphs_AreSeparated()
    {
        // InnerText concatenates the whole body with no separators, so two
        // paragraphs arrive as one sentence.
        var result = DocumentExtraction.Extract(Docx());

        Assert.Contains("First paragraph.\nSecond paragraph.", result.Text);
    }

    [Fact]
    public void DocxTable_KeepsItsCellAndRowBoundaries()
    {
        // Losing these turns a medication list into "Amlodipine5 mgRamipril10 mg",
        // which is the same failure the milestone refused when it declined to
        // rejoin hard-wrapped PDF lines.
        var result = DocumentExtraction.Extract(Docx());

        Assert.Contains("Amlodipine\t5 mg\nRamipril\t10 mg", result.Text);
    }

    [Fact]
    public void DocxTrackedChanges_YieldTheAcceptedView()
    {
        // The reason this rule exists, in one assertion. A naive walk of a
        // document whose author changed a dose from 5 mg to 10 mg produces
        // "Dose is 10 mg5 mg daily." — both values, adjacent, in clinical text.
        var result = DocumentExtraction.Extract(Docx());

        Assert.Contains("Dose is 10 mg daily.", result.Text);
        Assert.DoesNotContain("10 mg5 mg", result.Text);
    }

    [Fact]
    public void DocxHiddenText_IsExcluded()
    {
        // Marked vanish: not text the sender is showing anyone.
        var result = DocumentExtraction.Extract(Docx());

        Assert.DoesNotContain("HIDDEN-MARKER", result.Text);
    }

    [Fact]
    public void DocxHeader_IsIncluded()
    {
        // A referral's date and clinic often live only in the letterhead, and
        // dropping content the sender supplied is the silent loss this project
        // refuses elsewhere.
        var result = DocumentExtraction.Extract(Docx());

        Assert.Contains("Meadowbrook Oncology", result.Text);
    }

    [Fact]
    public void Xlsx_IsUnsupportedRatherThanCorrupt()
    {
        // Measured, not assumed: opening a spreadsheet as a WordprocessingDocument
        // succeeds and yields a MainDocumentPart, so "has a main part" is not
        // the discriminator — the content type is. Nothing is wrong with the
        // file; it is simply not one we read.
        var result = DocumentExtraction.Extract(Xlsx());

        Assert.Equal(DocumentExtractionOutcomes.UnsupportedType, result.Outcome);
    }

    [Fact]
    public void TruncatedDocx_IsCorrupt()
    {
        var whole = Docx();

        var result = DocumentExtraction.Extract(whole.Take(whole.Length / 3).ToArray());

        Assert.Equal(DocumentExtractionOutcomes.Corrupt, result.Outcome);
    }

    [Fact]
    public void ArchiveDeclaringEnormousContents_IsRefusedBeforeItIsOpened()
    {
        // A zip's cost is not its size. This one is a few hundred bytes and
        // declares far more, which the byte cap cannot see.
        var result = DocumentExtraction.Extract(ZipBomb());

        Assert.Equal(DocumentExtractionOutcomes.ExpandsTooLarge, result.Outcome);
    }

    // ---- copy ----------------------------------------------------------

    [Fact]
    public void EveryOutcomeHasCopyThatNamesTheCause()
    {
        // The refusal text is the whole of what a person is told, on both
        // doors (#236, #237), so a missing case is a user-facing hole.
        string[] outcomes =
        [
            DocumentExtractionOutcomes.UnsupportedType,
            DocumentExtractionOutcomes.Corrupt,
            DocumentExtractionOutcomes.PasswordProtected,
            DocumentExtractionOutcomes.NoTextLayer,
            DocumentExtractionOutcomes.Empty,
            DocumentExtractionOutcomes.TooLarge,
            DocumentExtractionOutcomes.TooManyPages,
            DocumentExtractionOutcomes.TooMuchText,
            DocumentExtractionOutcomes.TimedOut
        ];

        foreach (var outcome in outcomes)
        {
            var copy = DocumentExtractionCopy.For(outcome);

            Assert.NotEqual("This file could not be read.", copy);
            Assert.EndsWith(".", copy);
        }
    }

    // ---- fixtures ------------------------------------------------------

    private static byte[] TextPdf(string text, string? ownerPassword = null)
    {
        var document = new PdfDocument();
        var page = document.AddPage();

        using (var gfx = XGraphics.FromPdfPage(page))
        {
            gfx.DrawString(text, new XFont("Liberation Sans", 11), XBrushes.Black, new XPoint(50, 80));
        }

        if (ownerPassword != null)
        {
            // Owner password only: the user password stays empty, which is
            // what makes this the permissions-only case.
            document.SecuritySettings.OwnerPassword = ownerPassword;
            document.SecurityHandler.SetEncryptionToV5();
        }

        return Save(document);
    }

    /// <summary>
    /// A page bearing an image and no glyphs — what a scanned referral or a
    /// fax looks like. The image is a 1x1 JPEG inline rather than a checked-in
    /// file, keeping to this suite's no-committed-binaries convention.
    /// </summary>
    private static byte[] ImageOnlyPdf()
    {
        const string OnePixelJpeg =
            "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0a"
            + "HBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/wAALCAABAAEBAREA/8QAFAABAAAAAAAA"
            + "AAAAAAAAAAAACf/EABQQAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQEAAD8AKp//2Q==";

        var document = new PdfDocument();
        var page = document.AddPage();

        using (var gfx = XGraphics.FromPdfPage(page))
        using (var image = XImage.FromStream(new MemoryStream(Convert.FromBase64String(OnePixelJpeg))))
        {
            gfx.DrawImage(image, 50, 50, 400, 500);
        }

        return Save(document);
    }

    private static byte[] BlankPdf(int pages)
    {
        var document = new PdfDocument();

        for (var i = 0; i < pages; i++)
        {
            document.AddPage();
        }

        return Save(document);
    }

    /// <summary>
    /// A referral-shaped Word document carrying every case the walk has to get
    /// right: two paragraphs, a tracked dose change, a medication table,
    /// hidden text, and a letterhead in a header part.
    /// </summary>
    private static byte[] Docx()
    {
        using var buffer = new MemoryStream();

        using (var document = WordprocessingDocument.Create(buffer, WordprocessingDocumentType.Document, true))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document(new Body());
            var body = main.Document.Body!;

            body.Append(new Paragraph(new Run(new Text("First paragraph."))));
            body.Append(new Paragraph(new Run(new Text("Second paragraph."))));

            var dose = new Paragraph();
            dose.Append(new Run(new Text("Dose is ")));
            dose.Append(new InsertedRun(new Run(new Text("10 mg"))) { Author = "clinician", Id = "1" });
            dose.Append(new DeletedRun(new Run(new DeletedText("5 mg"))) { Author = "clinician", Id = "2" });
            dose.Append(new Run(new Text(" daily.")));
            body.Append(dose);

            var table = new Table();

            foreach (var (drug, dosage) in new[] { ("Amlodipine", "5 mg"), ("Ramipril", "10 mg") })
            {
                table.Append(new TableRow(
                    new TableCell(new Paragraph(new Run(new Text(drug)))),
                    new TableCell(new Paragraph(new Run(new Text(dosage))))));
            }

            body.Append(table);
            body.Append(new Paragraph(new Run(new RunProperties(new Vanish()), new Text("HIDDEN-MARKER"))));

            var header = main.AddNewPart<HeaderPart>();
            header.Header = new Header(new Paragraph(new Run(new Text("Meadowbrook Oncology"))));
        }

        return buffer.ToArray();
    }

    private static byte[] Xlsx()
    {
        using var buffer = new MemoryStream();

        using (var document = SpreadsheetDocument.Create(buffer, SpreadsheetDocumentType.Workbook, true))
        {
            document.AddWorkbookPart().Workbook = new DocumentFormat.OpenXml.Spreadsheet.Workbook(
                new DocumentFormat.OpenXml.Spreadsheet.Sheets());
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Highly compressible content whose declared uncompressed size dwarfs the
    /// archive — the shape a byte cap cannot catch.
    /// </summary>
    private static byte[] ZipBomb()
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var i = 0; i < 4; i++)
            {
                using var entry = new StreamWriter(archive.CreateEntry($"part{i}.xml").Open());
                entry.Write(new string('a', 40 * 1024 * 1024));
            }
        }

        return buffer.ToArray();
    }

    private static byte[] Save(PdfDocument document)
    {
        using var stream = new MemoryStream();
        document.Save(stream, closeStream: false);
        return stream.ToArray();
    }

    private sealed class EmbeddedLiberationSans : IFontResolver
    {
        public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
            new FontResolverInfo("LiberationSans-Regular");

        public byte[]? GetFont(string faceName)
        {
            using var stream = typeof(ConsultDocumentPdf).Assembly
                .GetManifestResourceStream($"Consultologist.Api.Fonts.{faceName}.ttf");

            if (stream == null)
            {
                return null;
            }

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
    }
}
