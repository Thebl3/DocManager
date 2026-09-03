using System.Text;
using DocManager.Photometry;

namespace DocManager.Signify;

public enum DownloadKind { Pdf, Ies }

public static class DownloadValidation
{
    public static bool IsValidFile(string path, string? contentType, DownloadKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (kind == DownloadKind.Ies)
        {
            try { _ = new IesParser().ParseFile(path); return true; }
            catch (Exception exception) when (exception is DecoderFallbackException or ArgumentException or IOException or InvalidDataException or NotSupportedException or OverflowException) { return false; }
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var prefix = new byte[(int)Math.Min(stream.Length, 4096L)];
        var length = stream.Read(prefix);
        return IsValid(prefix.AsSpan(0, length), contentType, kind);
    }

    public static bool IsValid(ReadOnlySpan<byte> content, string? contentType, DownloadKind kind)
    {
        if (content.IsEmpty) return false;
        var clean = content;
        if (clean.Length >= 3 && clean[0] == 0xEF && clean[1] == 0xBB && clean[2] == 0xBF) clean = clean[3..];
        while (!clean.IsEmpty && char.IsWhiteSpace((char)clean[0])) clean = clean[1..];
        if (kind == DownloadKind.Pdf)
        {
            return clean.StartsWith("%PDF"u8);
        }

        var prefix = Encoding.ASCII.GetString(clean[..Math.Min(clean.Length, 4096)]);
        if (prefix.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
            prefix.StartsWith("<HTML", StringComparison.OrdinalIgnoreCase) ||
            prefix.StartsWith("<?XML", StringComparison.OrdinalIgnoreCase) ||
            prefix.StartsWith('{') || prefix.StartsWith('['))
        {
            return false;
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        foreach (var encoding in new Encoding[]
        {
            new UTF8Encoding(false, true),
            Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
            Encoding.GetEncoding(28591, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
        })
        {
            try
            {
                _ = new IesParser().Parse(encoding.GetString(content));
                return true;
            }
            catch (Exception exception) when (exception is DecoderFallbackException or ArgumentException or InvalidDataException or NotSupportedException or OverflowException) { }
        }
        return false;
    }
}
