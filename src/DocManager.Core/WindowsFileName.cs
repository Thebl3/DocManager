using System.Text.RegularExpressions;

namespace DocManager.Core;

public static partial class WindowsFileName
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    [GeneratedRegex("[<>:\"/\\\\|?*\\x00-\\x1F]", RegexOptions.CultureInvariant)]
    private static partial Regex InvalidCharactersRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhiteSpaceRegex();

    public static string Sanitize(string? value, string fallback = "Unknown", int maxLength = 120)
    {
        var clean = InvalidCharactersRegex().Replace(value ?? string.Empty, " ");
        clean = WhiteSpaceRegex().Replace(clean, " ").Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(clean))
        {
            clean = fallback;
        }

        if (Reserved.Contains(Path.GetFileNameWithoutExtension(clean)))
        {
            clean = $"_{clean}";
        }

        if (clean.Length > maxLength)
        {
            clean = clean[..maxLength].TrimEnd('.', ' ');
        }

        return clean;
    }
}
