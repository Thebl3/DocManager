using DocManager.Core;

namespace DocManager.Signify;

public static class SignifyTextInput
{
    public static string ReplaceLastQuery(string? text, string replacement) =>
        ReplaceQueryAtCaret(text, (text ?? string.Empty).Length, replacement).Text;

    public static TextReplacement ReplaceQueryAtCaret(string? text, int caretIndex, string replacement)
    {
        var source = text ?? string.Empty;
        var normalizedReplacement = replacement.Trim();
        if (normalizedReplacement.Length == 0 || !TryGetTokenRange(source, caretIndex, out var tokenStart, out var tokenEnd))
            return new(source, Math.Clamp(caretIndex, 0, source.Length));
        var result = source[..tokenStart] + normalizedReplacement + source[tokenEnd..];
        return new(result, tokenStart + normalizedReplacement.Length);
    }

    public static string CurrentQuery(string? text) => CurrentQuery(text, (text ?? string.Empty).Length);

    public static string CurrentQuery(string? text, int caretIndex)
    {
        var source = text ?? string.Empty;
        return TryGetTokenRange(source, caretIndex, out var tokenStart, out var tokenEnd)
            ? source[tokenStart..tokenEnd].Trim()
            : string.Empty;
    }

    private static bool TryGetTokenRange(string source, int caretIndex, out int tokenStart, out int tokenEnd)
    {
        tokenStart = tokenEnd = 0;
        if (caretIndex < 0 || caretIndex > source.Length) return false;

        var position = caretIndex;
        if (position < source.Length && source[position] is '\r' or '\n')
        {
            if (position == 0 || source[position - 1] is '\r' or '\n') return false;
            position--;
        }
        else if (position == source.Length && position > 0 && source[position - 1] is '\r' or '\n')
        {
            return false;
        }

        var rawStart = position;
        while (rawStart > 0 && source[rawStart - 1] is not (',' or '\r' or '\n')) rawStart--;
        var rawEnd = position;
        while (rawEnd < source.Length && source[rawEnd] is not (',' or '\r' or '\n')) rawEnd++;

        tokenStart = rawStart;
        while (tokenStart < rawEnd && char.IsWhiteSpace(source[tokenStart])) tokenStart++;
        tokenEnd = rawEnd;
        while (tokenEnd > tokenStart && char.IsWhiteSpace(source[tokenEnd - 1])) tokenEnd--;
        return tokenStart < tokenEnd;
    }

    public readonly record struct TextReplacement(string Text, int CaretIndex);

    /// <summary>
    /// Gets the trimmed comma-delimited code token on the physical line containing the caret.
    /// A caret at a line or token end belongs to the preceding token; invalid indices return null.
    /// </summary>
    public static string? CodeAtCaret(string? text, int caretIndex)
    {
        var source = text ?? string.Empty;
        if (caretIndex < 0 || caretIndex > source.Length) return null;

        var lineStart = 0;
        var lineEnd = source.Length;
        for (var index = 0; index < source.Length; index++)
        {
            if (source[index] is not ('\r' or '\n')) continue;
            var delimiterEnd = source[index] == '\r' && index + 1 < source.Length && source[index + 1] == '\n'
                ? index + 1
                : index;
            if (caretIndex <= delimiterEnd)
            {
                lineEnd = index;
                break;
            }

            lineStart = delimiterEnd + 1;
            index = delimiterEnd;
        }

        var lineCaretIndex = Math.Min(caretIndex, lineEnd);
        for (var tokenStart = lineStart; ;)
        {
            var separator = source.IndexOf(',', tokenStart);
            if (separator < 0 || separator > lineEnd) separator = lineEnd;
            if (lineCaretIndex >= tokenStart && lineCaretIndex <= separator)
            {
                var token = source[tokenStart..separator].Trim();
                return token.Length == 0 ? null : token;
            }

            if (separator == lineEnd) return null;
            tokenStart = separator + 1;
        }
    }

    public static bool FileNameMatchesCode(string path, string code, string? canonicalProductName = null)
    {
        var key = TextNormalization.ProductKey(canonicalProductName ?? code);
        if (key.Length == 0) return false;
        var stem = ProductAssetName.StripLegacySkuPrefix(Path.GetFileNameWithoutExtension(path));
        var stemKey = TextNormalization.ProductKey(stem);
        if (stemKey.Equals(key, StringComparison.OrdinalIgnoreCase)) return true;

        // Product-name canonical assets require exact identity. Prefix matching is
        // retained only for numeric legacy SKU filenames.
        if (canonicalProductName is not null || !key.All(char.IsDigit)) return false;
        var unstripped = TextNormalization.ProductKey(Path.GetFileNameWithoutExtension(path));
        return unstripped.Equals(key, StringComparison.OrdinalIgnoreCase) ||
               unstripped.StartsWith(key + " ", StringComparison.OrdinalIgnoreCase);
    }
}
