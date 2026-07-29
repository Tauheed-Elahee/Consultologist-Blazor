using System.Text;

namespace Consultologist.Api.Documents;

/// <summary>
/// Turns the bytes of a text document into a string (#235, #242).
///
/// Text has no magic number, so this is the parser's fallback branch as
/// well as its decoder: bytes that cannot plausibly be text are reported as
/// such rather than decoded into replacement characters. Email intake used
/// to call Encoding.UTF8.GetString blindly, which substitutes U+FFFD rather
/// than throwing — so a UTF-16 or Windows-1252 referral became mojibake and
/// a consult was generated from it, silently (docs/DOCUMENT_INPUT.md § 3).
/// </summary>
internal static class TextDocumentDecoder
{
    internal const string ExtractorId = "text/1";

    private static readonly object ProviderLock = new();
    private static bool _providerRegistered;

    /// <summary>
    /// Windows-1252 is not one of the encodings .NET registers by default —
    /// Encoding.GetEncoding(1252) throws NotSupportedException until the
    /// code-pages provider is registered. On .NET 10 the provider ships in
    /// the shared framework, so this needs no package reference, only the
    /// call. Same one-time-under-a-lock shape as ConsultDocumentPdf's font
    /// resolver.
    /// </summary>
    private static void EnsureCodePagesProvider()
    {
        lock (ProviderLock)
        {
            if (_providerRegistered)
            {
                return;
            }

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _providerRegistered = true;
        }
    }

    /// <summary>
    /// True when the bytes are plausibly text. A byte-order mark settles it
    /// outright; otherwise an embedded NUL means binary.
    /// </summary>
    /// <remarks>
    /// The BOM check must come first. UTF-16 text is full of NUL bytes, so
    /// testing for NUL before reading the mark would reject every UTF-16
    /// file as binary.
    /// </remarks>
    internal static bool LooksLikeText(byte[] bytes)
    {
        if (BomLength(bytes) > 0)
        {
            return true;
        }

        return Array.IndexOf(bytes, (byte)0) < 0;
    }

    /// <summary>
    /// Decodes, honouring a byte-order mark when present. Without one:
    /// strict UTF-8 first, then Windows-1252. Deterministic by
    /// construction — the same bytes always yield the same string, which is
    /// what the effective-input hash needs. No statistical charset
    /// detection: it would decode identically only by luck, and differently
    /// across library versions.
    /// </summary>
    internal static string Decode(byte[] bytes)
    {
        var bom = BomLength(bytes);

        if (bom > 0)
        {
            return BomEncoding(bytes).GetString(bytes, bom, bytes.Length - bom);
        }

        try
        {
            // throwOnInvalidBytes: the point is to learn that this is not
            // UTF-8, not to paper over it with replacement characters.
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            EnsureCodePagesProvider();

            // Windows-1252 rather than Latin-1: they differ exactly in the
            // 0x80-0x9F range, which is where the smart quotes, en/em
            // dashes and ellipses of anything exported from Word live.
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
    }

    private static int BomLength(byte[] bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            return 4;
        }

        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            return 4;
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return 3;
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return 2;
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return 2;
        }

        return 0;
    }

    private static Encoding BomEncoding(byte[] bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            return new UTF32Encoding(bigEndian: false, byteOrderMark: false);
        }

        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            return new UTF32Encoding(bigEndian: true, byteOrderMark: false);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return new UTF8Encoding(false);
        }

        return bytes[0] == 0xFF
            ? new UnicodeEncoding(bigEndian: false, byteOrderMark: false)
            : new UnicodeEncoding(bigEndian: true, byteOrderMark: false);
    }
}
