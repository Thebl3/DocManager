using System.Globalization;
using System.Text.RegularExpressions;

namespace DocManager.Core;

public static partial class LocalizedNumber
{
    [GeneratedRegex(@"[-+]?\d[\d\s.,]*", RegexOptions.CultureInvariant)]
    private static partial Regex NumericTokenRegex();

    public static bool TryParse(string? value, out double result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var match = NumericTokenRegex().Match(value.Replace(' ', ' ').Trim());
        if (!match.Success)
        {
            return false;
        }

        var token = match.Value.Replace(" ", string.Empty, StringComparison.Ordinal);
        var comma = token.LastIndexOf(',');
        var dot = token.LastIndexOf('.');
        char? decimalSeparator = null;

        if (comma >= 0 && dot >= 0)
        {
            decimalSeparator = comma > dot ? ',' : '.';
        }
        else if (comma >= 0 || dot >= 0)
        {
            var separator = comma >= 0 ? ',' : '.';
            var index = Math.Max(comma, dot);
            var digitsAfter = token.Length - index - 1;
            var occurrences = token.Count(c => c == separator);
            decimalSeparator = occurrences == 1 && digitsAfter is > 0 and <= 2 ? separator : null;
        }

        if (decimalSeparator is not null)
        {
            var thousands = decimalSeparator == ',' ? '.' : ',';
            token = token.Replace(thousands.ToString(), string.Empty, StringComparison.Ordinal)
                         .Replace(decimalSeparator.Value, '.');
        }
        else
        {
            token = token.Replace(",", string.Empty, StringComparison.Ordinal)
                         .Replace(".", string.Empty, StringComparison.Ordinal);
        }

        return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    public static double? ParseNullable(string? value) => TryParse(value, out var parsed) ? parsed : null;
}
