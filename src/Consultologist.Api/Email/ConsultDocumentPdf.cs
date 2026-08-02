using System.Reflection;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;
using PdfSharp.Fonts;
using PdfSharp.Pdf.Security;
using Microsoft.Extensions.Logging;

namespace Consultologist.Api.Email;

/// <summary>
/// #159: renders the assembled consult document (the normative v6 markdown —
/// "## " section headings, blank-line separators, free-form agent prose
/// inside) to a password-protected PDF (AES-256, PDF 2.0 encryption V5).
/// The markdown pipeline mirrors the client's MarkdownPreview: HTML disabled,
/// soft line breaks hard — so the PDF reads like the in-app preview. The
/// password protects the document in the mailbox; it is not end-to-end
/// secrecy from the server, which produced the plaintext
/// (docs/ASYNC_DELIVERY.md §3).
/// </summary>
internal static class ConsultDocumentPdf
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    private static readonly object FontResolverLock = new();
    private static FontGlyphCoverage? _coverage;

    internal static byte[] Render(string markdown, string password, ILogger? logger = null)
    {
        EnsureFontResolver();

        // #252: what the embedded font can actually draw. Characters it
        // cannot are folded onto identical marks it can, and whatever is left
        // is counted so it stops being silent.
        var coverage = EnsureCoverage();
        var prepared = RenderableText.Prepare(markdown, coverage);

        if (prepared.Unrenderable.Count > 0)
        {
            logger?.LogWarning(
                "Consult PDF contains characters the embedded font cannot draw; they will render as missing glyphs. Codepoints={Codepoints}",
                RenderableText.Describe(prepared.Unrenderable));
        }

        markdown = prepared.Text;

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

        foreach (var block in Markdig.Markdown.Parse(markdown, Pipeline))
        {
            AppendBlock(section, block, indentLevel: 0);
        }

        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();

        var pdf = renderer.PdfDocument;

        // A generic title: it names the document in a mail client's preview
        // and in a reader's title bar, and must therefore carry nothing about
        // the patient (docs/ASYNC_DELIVERY.md § 3).
        pdf.Info.Title = "Consult";

        // #252: the document language, for screen readers and for any reader
        // deciding how to hyphenate or pronounce it. PDFsharp exposes no
        // typed property, so it goes on the catalog directly.
        pdf.Internals.Catalog.Elements.SetString("/Lang", "en-CA");

        var securitySettings = pdf.SecuritySettings;
        securitySettings.UserPassword = password;
        securitySettings.OwnerPassword = password;
        pdf.SecurityHandler.SetEncryptionToV5();

        using var stream = new MemoryStream();
        pdf.Save(stream, closeStream: false);
        return stream.ToArray();
    }

    private static void AppendBlock(Section section, Block block, int indentLevel)
    {
        switch (block)
        {
            case HeadingBlock heading:
                var headingParagraph = section.AddParagraph();
                headingParagraph.Format.Font.Bold = true;
                headingParagraph.Format.Font.Size = heading.Level switch
                {
                    1 => 15,
                    2 => 13,
                    _ => 11.5
                };
                headingParagraph.Format.SpaceBefore = Unit.FromPoint(10);
                headingParagraph.Format.SpaceAfter = Unit.FromPoint(4);
                AppendInlines(headingParagraph, heading.Inline);
                break;

            case ParagraphBlock paragraph:
                var bodyParagraph = section.AddParagraph();
                if (indentLevel > 0)
                {
                    bodyParagraph.Format.LeftIndent = Unit.FromCentimeter(0.5 * indentLevel);
                }

                AppendInlines(bodyParagraph, paragraph.Inline);
                break;

            case ListBlock list:
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    var first = true;
                    foreach (var child in item)
                    {
                        if (first && child is ParagraphBlock itemParagraph)
                        {
                            var listParagraph = section.AddParagraph();
                            listParagraph.Format.LeftIndent = Unit.FromCentimeter(0.5 * (indentLevel + 1));
                            listParagraph.Format.SpaceAfter = Unit.FromPoint(2);
                            listParagraph.AddText(list.IsOrdered ? $"{item.Order}. " : "• ");
                            AppendInlines(listParagraph, itemParagraph.Inline);
                            first = false;
                        }
                        else
                        {
                            AppendBlock(section, child, indentLevel + 1);
                        }
                    }
                }

                break;

            case QuoteBlock quote:
                foreach (var child in quote)
                {
                    AppendBlock(section, child, indentLevel + 1);
                }

                break;

            case CodeBlock code:
                var codeParagraph = section.AddParagraph();
                codeParagraph.Format.LeftIndent = Unit.FromCentimeter(0.5 * (indentLevel + 1));
                codeParagraph.AddText(code.Lines.ToString());
                break;

            case ThematicBreakBlock:
                var ruleParagraph = section.AddParagraph();
                ruleParagraph.Format.SpaceBefore = Unit.FromPoint(6);
                ruleParagraph.Format.SpaceAfter = Unit.FromPoint(6);
                ruleParagraph.AddText(new string('—', 12));
                break;

            default:
                // Conservative fallback: unhandled markdown renders as its raw
                // source text rather than being dropped.
                var span = block.Span;
                if (span.Length > 0)
                {
                    section.AddParagraph();
                }

                break;
        }
    }

    private static void AppendInlines(Paragraph paragraph, ContainerInline? inlines)
    {
        if (inlines == null)
        {
            return;
        }

        foreach (var inline in inlines)
        {
            AppendInline(paragraph, inline, bold: false, italic: false);
        }
    }

    private static void AppendInline(Paragraph paragraph, Inline inline, bool bold, bool italic)
    {
        switch (inline)
        {
            case LiteralInline literal:
                AddText(paragraph, literal.Content.ToString(), bold, italic);
                break;

            case EmphasisInline emphasis:
                var childBold = bold || emphasis.DelimiterCount >= 2;
                var childItalic = italic || emphasis.DelimiterCount == 1;
                foreach (var child in emphasis)
                {
                    AppendInline(paragraph, child, childBold, childItalic);
                }

                break;

            case LineBreakInline:
                paragraph.AddLineBreak();
                break;

            case CodeInline code:
                AddText(paragraph, code.Content, bold, italic);
                break;

            case LinkInline link:
                // Text only — the PDF is a clinical document, not hypertext.
                foreach (var child in link)
                {
                    AppendInline(paragraph, child, bold, italic);
                }

                break;

            case ContainerInline container:
                foreach (var child in container)
                {
                    AppendInline(paragraph, child, bold, italic);
                }

                break;

            default:
                var text = inline.ToString();
                if (!string.IsNullOrEmpty(text))
                {
                    AddText(paragraph, text, bold, italic);
                }

                break;
        }
    }

    private static void AddText(Paragraph paragraph, string text, bool bold, bool italic)
    {
        if (text.Length == 0)
        {
            return;
        }

        var formatted = paragraph.AddFormattedText(text);
        formatted.Bold = bold;
        formatted.Italic = italic;
    }

    private static void EnsureFontResolver()
    {
        lock (FontResolverLock)
        {
            GlobalFontSettings.FontResolver ??= new LiberationSansFontResolver();
        }
    }

    /// <summary>
    /// Read once from the same bytes the resolver embeds, so coverage can
    /// never disagree with the font actually in the file.
    /// </summary>
    private static FontGlyphCoverage EnsureCoverage()
    {
        lock (FontResolverLock)
        {
            if (_coverage != null)
            {
                return _coverage;
            }

            var bytes = new LiberationSansFontResolver().GetFont("LiberationSans-Regular");
            _coverage = bytes == null
                ? FontGlyphCoverage.Read([])
                : FontGlyphCoverage.Read(bytes);
            return _coverage;
        }
    }

    /// <summary>
    /// PDFsharp Core resolves no system fonts on Linux; Liberation Sans (SIL
    /// OFL) is embedded in the assembly (Fonts/, LICENSE alongside).
    /// </summary>
    private sealed class LiberationSansFontResolver : IFontResolver
    {
        public string DefaultFontName => "Liberation Sans";

        public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
        {
            var face = (bold, italic) switch
            {
                (true, true) => "LiberationSans-BoldItalic",
                (true, false) => "LiberationSans-Bold",
                (false, true) => "LiberationSans-Italic",
                _ => "LiberationSans-Regular"
            };

            return new FontResolverInfo(face);
        }

        public byte[]? GetFont(string faceName)
        {
            var resourceName = $"Consultologist.Api.Fonts.{faceName}.ttf";
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);

            if (stream == null)
            {
                return null;
            }

            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
    }
}
