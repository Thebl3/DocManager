using System.Text.RegularExpressions;

namespace DocManager.Dialux;

internal static partial class AmbientTemperatureNormalizer
{
    public static string Normalize(string? value)
    {
        var normalized = NormalizePdfGlyphs(value);
        if (normalized.Length == 0) return string.Empty;

        var range = TemperatureRangeRegex().Match(normalized);
        if (range.Success)
        {
            var separator = range.Groups["separator"].Value;
            if (separator is "-" or "–" or "—") separator = VietnameseAmbientLabelRegex().IsMatch(normalized) ? "đến" : "to";
            return $"{range.Groups["low"].Value} {separator} {range.Groups["high"].Value}°C";
        }

        var temperature = TemperatureValueRegex().Match(normalized);
        if (temperature.Success) return $"{temperature.Groups["value"].Value}°C";

        normalized = AmbientLabelRegex().Replace(normalized, string.Empty);
        normalized = AmbientQualifierRegex().Replace(normalized, string.Empty);
        normalized = PdfControlRegex().Replace(normalized, string.Empty);
        return Regex.Replace(normalized, @"\s+", " ").Trim(' ', ':', '=', ';', ',');
    }

    private static string NormalizePdfGlyphs(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Replace('º', '°').Replace('˚', '°');
        normalized = DegreeBeforeCRegex().Replace(normalized, "°C");
        normalized = PdfControlRegex().Replace(normalized, " ");
        normalized = OrphanCombiningDegreeRegex().Replace(normalized, " ");
        return Regex.Replace(normalized, @"\s+", " ").Trim();
    }

    [GeneratedRegex(@"(?<=\d)[\p{Cc}\p{Cf}\p{Mn}\s]*(?:°|º|˚)?[\p{Cc}\p{Cf}\p{Mn}\s]*C\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DegreeBeforeCRegex();

    [GeneratedRegex(@"[\p{Cc}\p{Cf}]", RegexOptions.CultureInvariant)]
    private static partial Regex PdfControlRegex();

    [GeneratedRegex(@"\p{Mn}", RegexOptions.CultureInvariant)]
    private static partial Regex OrphanCombiningDegreeRegex();

    [GeneratedRegex(@"(?<low>[+-]?\d[\d.,]*)\s*(?:°\s*C\s*)?(?<separator>to|đến|[-–—])\s*(?<high>[+-]?\d[\d.,]*)\s*°\s*C\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TemperatureRangeRegex();

    [GeneratedRegex(@"(?<value>[+-]?\d[\d.,]*)\s*°\s*C\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TemperatureValueRegex();

    [GeneratedRegex(@"(?:Nhiệt\s+độ\s+môi\s+trường\s+hiệu\s+quả|Phạm\s+vi\s+nhiệt\s+độ\s+môi\s+trường\s+xung\s+quanh|Nhiệt\s+độ\s+môi\s+trường(?:\s+cho\s+phép)?|Nhiệt\s+độ\s+xung\s+quanh|Nhiệt\s+độ\s+hiệu\s+quả|(?:Nhiệt\s+độ\s+)?hiệu\s+quả|Dải\s+nhiệt\s+độ)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VietnameseAmbientLabelRegex();

    [GeneratedRegex(@"^\s*(?:Nhiệt\s+độ\s+môi\s+trường\s+hiệu\s+quả|Phạm\s+vi\s+nhiệt\s+độ\s+môi\s+trường\s+xung\s+quanh|Nhiệt\s+độ\s+môi\s+trường(?:\s+cho\s+phép)?|Nhiệt\s+độ\s+xung\s+quanh|Nhiệt\s+độ\s+hiệu\s+quả|(?:Nhiệt\s+độ\s+)?hiệu\s+quả|Dải\s+nhiệt\s+độ|Operating\s+temperature|Ambient\s+temperature(?:\s+range)?)\s*[:=;,]?\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AmbientLabelRegex();

    [GeneratedRegex(@"^\s*(?:\(\s*)?(?:Ta|Tq)(?:\s*\))?\s*[:=;,]?\s*(?=[+-]?\d[\d.,]*\s*°\s*C\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AmbientQualifierRegex();
}
