#!/usr/bin/env dotnet
#:package DocumentFormat.OpenXml@3.5.1
#:property ManagePackageVersionsCentrally=false

// Builds the two Word documents the document-intake verification runs use
// (#254, for #240).
//
// Usage:
//   dotnet run --file scripts/make-docx-fixtures.cs -- <output-directory>
//   dotnet run --file scripts/make-docx-fixtures.cs            # writes to the cwd
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
// Note that prior_notes.docx is NOT built from prior_notes.txt. Its content is
// structural and lives here; the .txt is a separate fixture for the text path.

using System.Runtime.CompilerServices;
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

if (!File.Exists(referralSource))
{
    // Deliberately fatal, with no fallback to embedded text. Substituting
    // different content would still produce a valid .docx — and a different
    // effective-input hash, quietly invalidating the comparison this fixture
    // exists to support. A loud failure is the only safe answer.
    Console.Error.WriteLine($"missing source text: {referralSource}");
    Console.Error.WriteLine("consult_draft.docx must hold the same text as the .txt and .pdf fixtures.");
    return 1;
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

return 0;

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
    Console.WriteLine($"  {new FileInfo(path).Length,7:N0} bytes  {Path.GetFileName(path)}");
}

static string ScriptDirectory([CallerFilePath] string path = "") =>
    Path.GetDirectoryName(path)!;
