#!/usr/bin/env dotnet
#:package DocumentFormat.OpenXml@3.5.1
#:property ManagePackageVersionsCentrally=false

// Builds the non-PDF input fixtures from scripts/fixtures/: the two Word
// documents the document-intake verification runs use (#254, for #240), and
// the two text variants of prior_notes.txt that show-extraction.sh sweeps by
// default (#256). The PDFs have their own script because they need a
// different library — see make-pdf-fixtures.cs.
//
// Usage:
//   dotnet run --file scripts/make-input-fixtures.cs -- <output-directory>
//   dotnet run --file scripts/make-input-fixtures.cs            # writes to the cwd
//
// Why this is committed rather than kept as a scratch file: #240's close-out
// rests on a production run against prior_notes.docx, and a verification whose
// artifact cannot be regenerated is not one anybody can re-check. The unit
// tests build their own packages in memory, which is right for them — but the
// path from a real .docx through both intake doors to a recorded origin has
// exactly one witness, and it should not live on one laptop.
//
// The fixtures are shaped deliberately, and neither shape is arbitrary:
//
//   consult_draft.docx  carries the same text as consult_draft.txt and the
//                       .pdf built from it, which is what makes a cross-door
//                       hash comparison mean anything. It also puts the clinic
//                       and date in a header part, so the decision to include
//                       headers and footers is visible in the extracted text
//                       rather than merely asserted.
//
//   prior_notes.docx    carries a tracked dose change and a medication table —
//                       the two cases the naive walk gets dangerously wrong. A
//                       plain InnerText walk of this document yields
//                       "Ramipril dose is 10 mg5 mg daily." and
//                       "Amlodipine5 mg", both values adjacent in clinical
//                       text. It is the only fixture with w:del and w:ins runs,
//                       so it is the only one that exercises the accepted-view
//                       path end to end.
//
//   prior_notes_utf16.txt   prior_notes.txt as UTF-16 LE with a BOM — the only
//                       fixture exercising the non-UTF-8 decode path (#242).
//
//   prior_notes_big.txt prior_notes.txt repeated past the character cap while
//                       staying under the byte cap — the only fixture that
//                       produces too-much-text rather than too-large.
//
// Note that prior_notes.docx is NOT built from prior_notes.txt, though the two
// text variants above are. The .docx content is structural — the tracked
// change and the table — and lives here, while the .txt is the source of the
// text path's fixtures.

using System.Runtime.CompilerServices;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

var outputDirectory = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Directory.GetCurrentDirectory();

if (!Directory.Exists(outputDirectory))
{
    Console.Error.WriteLine($"no such directory: {outputDirectory}");
    return 1;
}

// Resolved from this file's own path rather than the working directory, so the
// script finds its fixtures wherever it is invoked from.
var referralSource = Path.Combine(ScriptDirectory(), "fixtures", "consult_draft.txt");
var priorNotesSource = Path.Combine(ScriptDirectory(), "fixtures", "prior_notes.txt");

foreach (var source in new[] { referralSource, priorNotesSource })
{
    if (!File.Exists(source))
    {
        // Deliberately fatal, with no fallback to embedded text. Substituting
        // different content would still produce valid fixtures — of different
        // content, and a different effective-input hash, quietly invalidating
        // the comparisons they exist to support. A loud failure is the only
        // safe answer.
        Console.Error.WriteLine($"missing source text: {source}");
        Console.Error.WriteLine("the fixtures must hold the same text as the committed sources.");
        return 1;
    }
}

var referral = File.ReadAllText(referralSource);

// Fixed rather than DateTime.UtcNow. Revision dates are metadata, not text, so
// the extracted output and its hash are stable either way — but there is no
// reason for the bytes to differ on every run.
var revised = new DateTime(2026, 3, 14, 9, 0, 0, DateTimeKind.Utc);

Console.WriteLine($"writing to {outputDirectory}");

Write(Path.Combine(outputDirectory, "consult_draft.docx"), (body, main) =>
{
    foreach (var paragraph in referral.Split('\n'))
    {
        body.Append(new Paragraph(new Run(
            new Text(paragraph) { Space = SpaceProcessingModeValues.Preserve })));
    }

    var header = main.AddNewPart<HeaderPart>();
    header.Header = new Header(new Paragraph(new Run(
        new Text("Meadowbrook Oncology · 14 March 2026"))));
});

Write(Path.Combine(outputDirectory, "prior_notes.docx"), (body, _) =>
{
    body.Append(new Paragraph(new Run(new Text("Prior records, amended before sending."))));

    // The dose the author changed. Both runs are present in the file; only the
    // inserted one is the sender's intent.
    var dose = new Paragraph();
    dose.Append(new Run(new Text("Ramipril dose is ") { Space = SpaceProcessingModeValues.Preserve }));
    dose.Append(new InsertedRun(new Run(new Text("10 mg"))) { Author = "Dr Lee", Id = "1", Date = revised });
    dose.Append(new DeletedRun(new Run(new DeletedText("5 mg"))) { Author = "Dr Lee", Id = "2", Date = revised });
    dose.Append(new Run(new Text(" daily.") { Space = SpaceProcessingModeValues.Preserve }));
    body.Append(dose);

    // A table, because cell and row boundaries are meaning: without them the
    // medication list reads as one run-on string of drugs and doses.
    var table = new Table();

    foreach (var (drug, dosage) in new[] { ("Amlodipine", "5 mg"), ("Ramipril", "10 mg"), ("Calcium", "500 mg") })
    {
        table.Append(new TableRow(
            new TableCell(new Paragraph(new Run(new Text(drug)))),
            new TableCell(new Paragraph(new Run(new Text(dosage))))));
    }

    body.Append(table);
});

// The two text variants of prior_notes.txt. Not Word documents, but they
// belong to the same source and each is the only fixture reaching an outcome
// nothing else does — which is the same argument that made referral-scan.pdf
// worth a generator (#256).
var priorNotes = File.ReadAllText(priorNotesSource);

// UTF-16 LE with a BOM. The only fixture exercising the non-UTF-8 decode path:
// email intake called Encoding.UTF8.GetString blindly until #242, which
// substitutes U+FFFD rather than throwing, so a referral in this encoding
// became mojibake and a consult was generated from it silently. The BOM is
// what TextDocumentDecoder checks first — before the NUL-byte test, because
// UTF-16 is full of NULs and would otherwise read as binary.
WriteBytes(
    Path.Combine(outputDirectory, "prior_notes_utf16.txt"),
    new UnicodeEncoding(bigEndian: false, byteOrderMark: true).GetPreamble()
        .Concat(new UnicodeEncoding(bigEndian: false, byteOrderMark: true).GetBytes(priorNotes))
        .ToArray());

// Comfortably past the 256 KB character cap while staying well under the 10 MB
// byte cap, so this is the only fixture that produces too-much-text rather
// than too-large. The copy count is arbitrary; the gap between the two caps is
// the point, since a document can clear every byte bound and still hold more
// text than one input may take.
const int Copies = 4200;
WriteBytes(
    Path.Combine(outputDirectory, "prior_notes_big.txt"),
    Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(priorNotes + "\n", Copies))));

return 0;

static void WriteBytes(string path, byte[] bytes)
{
    File.WriteAllBytes(path, bytes);
    Console.WriteLine($"  {bytes.Length,9:N0} bytes  {Path.GetFileName(path)}");
}

static void Write(string path, Action<Body, MainDocumentPart> fill)
{
    using var buffer = new MemoryStream();

    using (var document = WordprocessingDocument.Create(buffer, WordprocessingDocumentType.Document, true))
    {
        var main = document.AddMainDocumentPart();
        main.Document = new Document(new Body());
        fill(main.Document.Body!, main);
    }

    File.WriteAllBytes(path, buffer.ToArray());
    Console.WriteLine($"  {new FileInfo(path).Length,9:N0} bytes  {Path.GetFileName(path)}");
}

static string ScriptDirectory([CallerFilePath] string path = "") =>
    Path.GetDirectoryName(path)!;
