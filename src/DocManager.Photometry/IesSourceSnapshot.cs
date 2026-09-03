using System.Security.Cryptography;
using System.Text;

#pragma warning disable SYSLIB0001

namespace DocManager.Photometry;

public enum IesSourceEncodingKind
{
    Utf8Bom,
    Utf8NoBom,
    Windows1252,
    Latin1
}

public enum IesNewLineKind
{
    None,
    CrLf,
    Lf,
    Cr,
    Mixed
}

public sealed record IesNewLineProfile(
    IesNewLineKind Kind,
    int CrLfCount,
    int LfCount,
    int CrCount,
    bool HasFinalNewLine)
{
    public string DisplayName => Kind switch
    {
        IesNewLineKind.CrLf => "CRLF",
        IesNewLineKind.Lf => "LF",
        IesNewLineKind.Cr => "CR",
        IesNewLineKind.Mixed => $"Mixed (CRLF {CrLfCount}, LF {LfCount}, CR {CrCount})",
        _ => "Không có"
    };

    internal static IesNewLineProfile Inspect(string text)
    {
        var crLf = 0;
        var lf = 0;
        var cr = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n')
                {
                    crLf++;
                    index++;
                }
                else
                {
                    cr++;
                }
            }
            else if (text[index] == '\n')
            {
                lf++;
            }
        }

        var kinds = (crLf > 0 ? 1 : 0) + (lf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
        var kind = kinds switch
        {
            0 => IesNewLineKind.None,
            > 1 => IesNewLineKind.Mixed,
            _ when crLf > 0 => IesNewLineKind.CrLf,
            _ when lf > 0 => IesNewLineKind.Lf,
            _ => IesNewLineKind.Cr
        };
        var hasFinalNewLine = text.EndsWith("\r\n", StringComparison.Ordinal) ||
                              text.EndsWith('\n') ||
                              text.EndsWith('\r');
        return new IesNewLineProfile(kind, crLf, lf, cr, hasFinalNewLine);
    }
}

/// <summary>
/// Immutable prepared IES input. DecodedText preserves source characters and
/// newline sequences exactly, excluding only the UTF-8 byte-order preamble.
/// Conversion uses Document from this snapshot and never writes the source.
/// </summary>
public sealed class IesSourceSnapshot
{
    private readonly byte[] _originalBytes;

    internal IesSourceSnapshot(
        string sourcePath,
        byte[] originalBytes,
        string decodedText,
        Encoding encoding,
        IesSourceEncodingKind encodingKind,
        IesDocument document,
        DateTime sourceLastWriteTimeUtc)
    {
        SourcePath = Path.GetFullPath(sourcePath);
        _originalBytes = originalBytes.ToArray();
        DecodedText = decodedText;
        Encoding = encoding;
        EncodingKind = encodingKind;
        NewLines = IesNewLineProfile.Inspect(decodedText);
        Document = document;
        SourceLastWriteTimeUtc = sourceLastWriteTimeUtc;
        Fingerprint = Convert.ToHexString(SHA256.HashData(_originalBytes));
    }

    public string SourcePath { get; }
    public byte[] OriginalBytes => _originalBytes.ToArray();
    public string DecodedText { get; }
    public Encoding Encoding { get; }
    public IesSourceEncodingKind EncodingKind { get; }
    public string EncodingDisplayName => EncodingKind switch
    {
        IesSourceEncodingKind.Utf8Bom => "UTF-8 (BOM)",
        IesSourceEncodingKind.Utf8NoBom => "UTF-8 (không BOM)",
        IesSourceEncodingKind.Windows1252 => "Windows-1252",
        _ => "ISO-8859-1 (Latin-1)"
    };
    public bool HasBom => EncodingKind == IesSourceEncodingKind.Utf8Bom;
    public IesNewLineProfile NewLines { get; }
    public bool HasFinalNewLine => NewLines.HasFinalNewLine;
    public int ByteCount => _originalBytes.Length;
    public string Fingerprint { get; }
    public DateTime SourceLastWriteTimeUtc { get; }
    public IesDocument Document { get; }
}

public sealed class IesSourceReader
{
    private readonly IesParser _parser;

    public IesSourceReader(IesParser? parser = null)
    {
        _parser = parser ?? new IesParser();
    }

    public IesSourceSnapshot ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var sourcePath = Path.GetFullPath(path);
        var bytes = File.ReadAllBytes(sourcePath);
        var (encoding, kind, text) = Decode(bytes);
        var document = _parser.Parse(text, sourcePath);
        return new IesSourceSnapshot(
            sourcePath,
            bytes,
            text,
            encoding,
            kind,
            document,
            File.GetLastWriteTimeUtc(sourcePath));
    }

    private static (Encoding Encoding, IesSourceEncodingKind Kind, string Text) Decode(byte[] bytes)
    {
        var preamble = Encoding.UTF8.GetPreamble();
        if (bytes.AsSpan().StartsWith(preamble))
        {
            var encoding = new UTF8Encoding(true, true);
            return (encoding, IesSourceEncodingKind.Utf8Bom, encoding.GetString(bytes, preamble.Length, bytes.Length - preamble.Length));
        }

        try
        {
            var encoding = new UTF8Encoding(false, true);
            return (encoding, IesSourceEncodingKind.Utf8NoBom, encoding.GetString(bytes));
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            // These byte values are undefined printable characters in Windows-1252.
            // Treating them as Latin-1 preserves their U+008x control code points and
            // makes the selected single-byte source encoding explicit.
            if (bytes.Any(value => value is 0x81 or 0x8D or 0x8F or 0x90 or 0x9D))
            {
                var latin1 = Encoding.GetEncoding(28591, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                return (latin1, IesSourceEncodingKind.Latin1, latin1.GetString(bytes));
            }

            var windows1252 = Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            return (windows1252, IesSourceEncodingKind.Windows1252, windows1252.GetString(bytes));
        }
    }
}
