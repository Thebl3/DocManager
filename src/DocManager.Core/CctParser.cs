using System.Text.RegularExpressions;

namespace DocManager.Core;

public static partial class CctParser
{
    [GeneratedRegex(@"(?<!\d)(?<kelvin>(?:[1-9]\d?[.,]\d{3}|[1-9]\d{3,4}))\s*(?:°\s*)?[Kk](?![A-Za-z])", RegexOptions.CultureInvariant)]
    private static partial Regex KelvinRegex();

    [GeneratedRegex(@"\b(?<code>[789][2-6][057])(?:PC)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ColorCodeRegex();

    [GeneratedRegex(@"(?:\bCCT\b|correlated\s+colou?r\s+temperature|colou?r\s+temperature|nhi\s*ệt\s+độ\s+màu(?:\s+tương\s+quan)?(?:\s*\([^\r\n)]*\))?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TechnicalLabelRegex();

    [GeneratedRegex(@"(?:available|options?|range|choice|selectable|tùy\s*chọn|tuỳ\s*chọn)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MarketingLabelRegex();

    public static int? ExtractStrict(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var rawLine in text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Trim();
            if (!TechnicalLabelRegex().IsMatch(line) || MarketingLabelRegex().IsMatch(line))
            {
                continue;
            }

            var matches = KelvinRegex().Matches(line);
            if (matches.Count == 1)
            {
                var normalized = matches[0].Groups["kelvin"].Value.Replace(",", string.Empty).Replace(".", string.Empty);
                if (int.TryParse(normalized, out var kelvin) && kelvin is >= 1000 and <= 50000)
                {
                    return kelvin;
                }
            }
        }

        return null;
    }

    public static int? FromProductCode(string? productCode)
    {
        if (string.IsNullOrWhiteSpace(productCode))
        {
            return null;
        }

        var match = ColorCodeRegex().Match(productCode.Replace('_', ' ').Replace('-', ' '));
        if (!match.Success)
        {
            return null;
        }

        var code = match.Groups["code"].Value;
        return int.TryParse(code.AsSpan(1, 2), out var hundreds) ? hundreds * 100 : null;
    }

    public static int? ExtractStrictOrProductCode(string? technicalText, string? productCode) =>
        ExtractStrict(technicalText) ?? FromProductCode(productCode);
}
