#!/usr/bin/env dotnet
#:package PDFsharp-MigraDoc@6.2.4
#:property ManagePackageVersionsCentrally=false

// Builds the two PDF fixtures the document-intake verification runs use
// (#256, for #235 and #238).
//
// Usage:
//   dotnet run --file scripts/make-pdf-fixtures.cs -- <output-directory>
//   dotnet run --file scripts/make-pdf-fixtures.cs            # writes to the cwd
//
// Both fixtures existed before this script and were produced by one that was
// never committed — their PDFsharp Producer string is still in them. That left
// referral-scan.pdf as the only artifact in existence producing the
// no-text-layer outcome, which § 8's failure copy and check 3 of
// verify-document-provenance.sh both depend on. One witness, and nobody could
// make another.
//
//   referral-text.pdf   the same referral as scripts/fixtures/consult_draft.txt,
//                       laid out by MigraDoc. Rendered rather than drawn
//                       because XGraphics.DrawString does not wrap: the text
//                       has to flow into lines for this to be a realistic
//                       text layer rather than one enormous clipped line.
//                       Extraction reflows it, so its text is NOT identical to
//                       the .txt — that is expected, and the reason this
//                       fixture is not used for a cross-format hash check.
//
//   referral-scan.pdf   pages, an image, and no glyphs at all — what a scan or
//                       a fax looks like, and the only way to reach
//                       no-text-layer. The image is a 1x1 JPEG inline rather
//                       than a checked-in file, matching the no-committed-
//                       binaries convention DocumentExtractionTests already
//                       follows for the same fixture shape.
//
// The font is read off disk from src/Consultologist.Api/Fonts. PDFsharp Core
// resolves no system fonts on Linux, and the app embeds these as assembly
// resources; a script cannot reach those, but the .ttf files are committed.

using System.Runtime.CompilerServices;
using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

var outputDirectory = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Directory.GetCurrentDirectory();

if (!Directory.Exists(outputDirectory))
{
    Console.Error.WriteLine($"no such directory: {outputDirectory}");
    return 1;
}

var scripts = ScriptDirectory();
var repository = Path.GetFullPath(Path.Combine(scripts, ".."));
var referralSource = Path.Combine(scripts, "fixtures", "consult_draft.txt");
var fontDirectory = Path.Combine(repository, "src", "Consultologist.Api", "Fonts");

if (!File.Exists(referralSource))
{
    // Fatal rather than falling back to embedded text, for the same reason
    // make-docx-fixtures.cs is: a substitution would still produce a valid
    // PDF, of different content, and the fixture would quietly stop being the
    // referral the other fixtures carry.
    Console.Error.WriteLine($"missing source text: {referralSource}");
    return 1;
}

if (!Directory.Exists(fontDirectory))
{
    Console.Error.WriteLine($"missing fonts: {fontDirectory}");
    return 1;
}

GlobalFontSettings.FontResolver = new DiskLiberationSans(fontDirectory);

Console.WriteLine($"writing to {outputDirectory}");

WriteTextPdf(Path.Combine(outputDirectory, "referral-text.pdf"), File.ReadAllText(referralSource));
WriteScanPdf(Path.Combine(outputDirectory, "referral-scan.pdf"));

return 0;

// Mirrors ConsultDocumentPdf.Render's setup — same font, same margins, same
// renderer — so this fixture exercises the font embedding the delivered PDFs
// use. That makes it a reproduction for #252 as well as an intake fixture.
static void WriteTextPdf(string path, string referral)
{
    var document = new Document();
    var section = document.Sections.AddSection();
    section.PageSetup.TopMargin = Unit.FromCentimeter(2);
    section.PageSetup.BottomMargin = Unit.FromCentimeter(2);
    section.PageSetup.LeftMargin = Unit.FromCentimeter(2.2);
    section.PageSetup.RightMargin = Unit.FromCentimeter(2.2);

    var normal = document.Styles[StyleNames.Normal]!;
    normal.Font.Name = "Liberation Sans";
    normal.Font.Size = 10.5;
    normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(6);

    // Blank lines separate paragraphs in the source; MigraDoc wraps each one.
    foreach (var paragraph in referral.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
    {
        section.AddParagraph(paragraph.Replace('\n', ' ').Trim());
    }

    var renderer = new PdfDocumentRenderer { Document = document };
    renderer.RenderDocument();

    // Page count before the save: PDFsharp treats saving as consuming the
    // in-memory document and throws on any read afterwards.
    var pages = renderer.PdfDocument.PageCount;
    renderer.PdfDocument.Save(path);

    Report(path, pages);
}

static void WriteScanPdf(string path)
{
    // A single black pixel, stretched over the page. Enough to make the page
    // carry an image and no glyphs, which is exactly what separates
    // no-text-layer from empty in PdfDocumentExtractor.
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

    var pages = document.PageCount;
    document.Save(path);

    Report(path, pages);
}

static void Report(string path, int pages) =>
    Console.WriteLine($"  {new FileInfo(path).Length,7:N0} bytes  {Path.GetFileName(path),-20} {pages} page{(pages == 1 ? "" : "s")}");

static string ScriptDirectory([CallerFilePath] string path = "") =>
    Path.GetDirectoryName(path)!;

/// <summary>
/// What ConsultDocumentPdf.LiberationSansFontResolver does, reading the .ttf
/// from the repository instead of from assembly resources.
/// </summary>
internal sealed class DiskLiberationSans(string directory) : IFontResolver
{
    public string DefaultFontName => "Liberation Sans";

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic) =>
        new((bold, italic) switch
        {
            (true, true) => "LiberationSans-BoldItalic",
            (true, false) => "LiberationSans-Bold",
            (false, true) => "LiberationSans-Italic",
            _ => "LiberationSans-Regular"
        });

    public byte[]? GetFont(string faceName)
    {
        var path = Path.Combine(directory, $"{faceName}.ttf");
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }
}
