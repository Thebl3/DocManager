using System.Globalization;
using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

namespace DocManager.Core;

public static partial class ProductAssetName
{
    public const int MaximumBaseNameLength = 120;

    [GeneratedRegex(@"^[0-9]{3,18}\s+-\s+", RegexOptions.CultureInvariant, 100)]
    private static partial Regex LegacySkuPrefixRegex();

    [GeneratedRegex(@"_auto(?:[2-9][0-9]*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex GeneratedSuffixRegex();

    private static readonly SearchValues<char> InvalidWindowsCharacters = SearchValues.Create("<>:\"/\\|?*");

    public static string CanonicalProductName(string? productName, string fallback = "Unknown")
    {
        if (productName?.Length > 32_768) productName = productName[..32_768];
        var builder = new StringBuilder(productName?.Length ?? 0);
        foreach (var rune in (productName ?? string.Empty).EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format ||
                (rune.IsAscii && InvalidWindowsCharacters.Contains((char)rune.Value)))
            {
                builder.Append(' ');
            }
            else
            {
                builder.Append(rune.ToString());
            }
        }

        var collapsed = string.Join(' ', builder.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return WindowsFileName.Sanitize(collapsed, fallback, MaximumBaseNameLength);
    }

    public static string CanonicalBaseName(string? productName, string fallback = "Unknown") =>
        CanonicalProductName(StripLegacySkuPrefix(productName), fallback);

    public static string StripLegacySkuPrefix(string? value) =>
        LegacySkuPrefixRegex().Replace((value ?? string.Empty).Trim(), string.Empty, 1);

    public static string FromVerifiedMatch(string? matchedName, string? fallbackDescription)
    {
        var source = !string.IsNullOrWhiteSpace(matchedName)
            ? matchedName
            : fallbackDescription;
        return CanonicalProductName(StripLegacySkuPrefix(source));
    }

    public static bool IsGeneratedLdtStem(string? stem) => GeneratedSuffixRegex().IsMatch(stem ?? string.Empty);

    public static ProductAssetResolution Resolve(
        IEnumerable<string> paths,
        string productName,
        bool allowGeneratedLdtSuffix = false)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var candidates = paths.Select(path =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(path);
                return Path.GetFullPath(path);
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var canonical = CanonicalBaseName(productName);
        var exact = candidates.Where(path =>
                Path.GetFileNameWithoutExtension(path).Equals(canonical, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (exact.Length == 1) return new ProductAssetResolution(exact[0], false, false);
        if (exact.Length > 1) return new ProductAssetResolution(null, true, false);

        var expectedKey = TextNormalization.ProductKey(CanonicalProductName(productName));
        if (expectedKey.Length == 0) return new ProductAssetResolution(null, false, false);
        var legacy = candidates.Where(path =>
            {
                var stem = Path.GetFileNameWithoutExtension(path);
                if (allowGeneratedLdtSuffix)
                {
                    var generated = GeneratedSuffixRegex().Match(stem);
                    if (generated.Success) stem = stem[..generated.Index];
                    else if (stem.Contains("_auto", StringComparison.OrdinalIgnoreCase)) return false;
                }
                return TextNormalization.ProductKey(StripLegacySkuPrefix(stem)).Equals(expectedKey, StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (legacy.Length > 0)
        {
            return legacy.Length == 1
                ? new ProductAssetResolution(legacy[0], false, true)
                : new ProductAssetResolution(null, true, true);
        }

        // Canonical naming no longer has the SKU prefix. For pre-existing
        // assets, resolve the numeric code through the product basename and
        // leave final SKU disambiguation to the caller's pricelist record.
        var numeric = expectedKey.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (numeric.Length == 0 || !numeric.All(char.IsDigit)) return new ProductAssetResolution(null, false, false);
        var numericPrefix = candidates.Where(path =>
            {
                var stem = Path.GetFileNameWithoutExtension(path);
                var separator = stem.IndexOf(" - ", StringComparison.Ordinal);
                return separator > 0 && stem[..separator].Equals(numeric, StringComparison.Ordinal);
            })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return numericPrefix.Length switch
        {
            0 => new ProductAssetResolution(null, false, false),
            1 => new ProductAssetResolution(numericPrefix[0], false, true),
            _ => new ProductAssetResolution(null, true, true)
        };
    }
}

public sealed record ProductAssetResolution(string? Path, bool HasCollision, bool IsLegacy);
