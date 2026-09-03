using DocManager.Core;

namespace DocManager.Signify;

public enum CatalogueProductResolutionStatus
{
    Found,
    Missing,
    Ambiguous
}

public sealed record CatalogueProductResolution(CatalogueProductResolutionStatus Status, ProductRecord? Record)
{
    public static CatalogueProductResolution Found(ProductRecord record) => new(CatalogueProductResolutionStatus.Found, record);
    public static CatalogueProductResolution Missing() => new(CatalogueProductResolutionStatus.Missing, null);
    public static CatalogueProductResolution Ambiguous() => new(CatalogueProductResolutionStatus.Ambiguous, null);
}

/// <summary>
/// Selects one pricelist product only when exact material, exact description, or a unique
/// description prefix identifies a single logical product. Numeric input is material-only:
/// it never performs description-prefix matching. Duplicate source rows never turn a unique
/// product into an ambiguity.
/// </summary>
public static class CatalogueProductResolver
{
    public static CatalogueProductResolution Resolve(IEnumerable<ProductRecord> records, string? code)
    {
        ArgumentNullException.ThrowIfNull(records);
        var key = TextNormalization.ProductKey(code);
        if (key.Length == 0) return CatalogueProductResolution.Missing();

        var items = records as IReadOnlyList<ProductRecord> ?? records.ToArray();
        var material = ResolveCandidates(items.Where(record =>
            TextNormalization.ProductKey(record.Material).Equals(key, StringComparison.OrdinalIgnoreCase)));
        if (material is not null) return material;

        // A numeric query is an explicit material identity, never a fuzzy or partial
        // description lookup. This preserves variant isolation such as 840 versus 865.
        if (key.All(char.IsDigit)) return CatalogueProductResolution.Missing();

        return ResolveCandidates(items.Where(record =>
                TextNormalization.ProductKey(record.MaterialDescription).Equals(key, StringComparison.OrdinalIgnoreCase)))
            ?? ResolveCandidates(items.Where(record =>
                TextNormalization.ProductKey(record.MaterialDescription).StartsWith(key + " ", StringComparison.OrdinalIgnoreCase)))
            ?? CatalogueProductResolution.Missing();
    }

    private static CatalogueProductResolution? ResolveCandidates(IEnumerable<ProductRecord> records)
    {
        var candidates = records
            .GroupBy(record =>
                $"{WindowsFileName.Sanitize(record.ProductFamily)}{TextNormalization.ProductKey(record.Material)}{TextNormalization.ProductKey(record.MaterialDescription)}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(2)
            .ToArray();

        return candidates.Length switch
        {
            0 => null,
            1 => CatalogueProductResolution.Found(candidates[0]),
            _ => CatalogueProductResolution.Ambiguous()
        };
    }
}
