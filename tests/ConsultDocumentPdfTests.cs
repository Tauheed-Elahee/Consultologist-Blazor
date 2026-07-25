using Consultologist.Api.Email;
using PdfSharp.Pdf.IO;

namespace Consultologist.Api.Tests;

public class ConsultDocumentPdfTests
{
    private const string Document = "## History of Present Illness\n\nThe patient presents with **worsening** dyspnea.\nSecond line kept hard-broken.\n\n## Plan\n\n- Echo\n- Repeat labs";
    private const string Password = "correct-horse-battery-16";

    [Fact]
    public void Render_ProducesAPdf()
    {
        var bytes = ConsultDocumentPdf.Render(Document, Password);

        Assert.True(bytes.Length > 1000);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
    }

    [Fact]
    public void Render_OpensWithTheCorrectPassword()
    {
        var bytes = ConsultDocumentPdf.Render(Document, Password);

        using var stream = new MemoryStream(bytes);
        using var reopened = PdfReader.Open(stream, Password, PdfDocumentOpenMode.ReadOnly);

        Assert.True(reopened.PageCount >= 1);
    }

    [Fact]
    public void Render_RejectsAWrongPassword()
    {
        var bytes = ConsultDocumentPdf.Render(Document, Password);

        using var stream = new MemoryStream(bytes);
        Assert.ThrowsAny<Exception>(() =>
            PdfReader.Open(stream, "wrong-password-16char", PdfDocumentOpenMode.ReadOnly));
    }

    [Fact]
    public void Render_RejectsOpeningWithoutAPassword()
    {
        var bytes = ConsultDocumentPdf.Render(Document, Password);

        using var stream = new MemoryStream(bytes);
        Assert.ThrowsAny<Exception>(() =>
            PdfReader.Open(stream, PdfDocumentOpenMode.ReadOnly));
    }

    [Fact]
    public void Render_HandlesPlainProseWithoutMarkdown()
    {
        var bytes = ConsultDocumentPdf.Render("Just a plain paragraph of prose.", Password);

        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
    }
}
