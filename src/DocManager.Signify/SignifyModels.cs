using System.Text.Json;
using DocManager.Core;
using DocManager.Photometry;

namespace DocManager.Signify;

public sealed record SignifySearchCandidate
{
    public string Sku { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string DisplayedDescription { get; init; } = string.Empty;
    public IReadOnlyList<string> ProductCodes { get; init; } = [];
    public string ProductUrl { get; init; } = string.Empty;
    public string LeafletUrl { get; init; } = string.Empty;
    public string IesUrl { get; init; } = string.Empty;
    public int Score { get; init; }
}

public sealed record ProductMatch
{
    public string QueryUsed { get; init; } = string.Empty;
    public string MatchedSku { get; init; } = string.Empty;
    public string MatchedName { get; init; } = string.Empty;
    public string ProductUrl { get; init; } = string.Empty;
    public string LeafletUrl { get; init; } = string.Empty;
    public IReadOnlyList<string> LeafletUrlCandidates { get; init; } = [];
    public string IesUrl { get; init; } = string.Empty;
    public int Score { get; init; }
    public bool IsExact { get; init; }
}

public sealed record DownloadOptions
{
    public required string OutputRoot { get; init; }
    public string Locale { get; init; } = "vi_VN";
    public int Retries { get; init; } = 3;
    public int MaxSearchPages { get; init; } = 10;
    public TimeSpan PerItemDelay { get; init; } = TimeSpan.Zero;
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(400);
    public int MaxConcurrency { get; init; } = 4;
    public bool Force { get; init; }
    public bool DownloadIes { get; init; } = true;
}

public enum DownloadAssetKind
{
    Pdf,
    Ies,
    Ldt
}

public enum DownloadAssetStatus
{
    Downloaded,
    Reused,
    Unavailable,
    Skipped,
    Failed
}

public sealed record DownloadAssetResult(
    DownloadAssetKind Kind,
    DownloadAssetStatus Status,
    string FilePath = "",
    string Detail = "");

public sealed record DownloadLogEntry
{
    public string Sheet { get; init; } = string.Empty;
    public int ExcelRow { get; init; }
    public string ProductFamily { get; init; } = string.Empty;
    public string Material { get; init; } = string.Empty;
    public string MaterialDescription { get; init; } = string.Empty;
    public string QueryUsed { get; init; } = string.Empty;
    public string MatchedSku { get; init; } = string.Empty;
    public string MatchedName { get; init; } = string.Empty;
    public string ProductUrl { get; init; } = string.Empty;
    public string LeafletUrl { get; init; } = string.Empty;
    public string OutputFile { get; init; } = string.Empty;
    public string IesUrl { get; init; } = string.Empty;
    public string IesFile { get; init; } = string.Empty;
    public string LdtFile { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
    // Kept outside the stable 16-column CSV contract for UI and in-memory diagnostics.
    public IReadOnlyList<DownloadAssetResult> AssetResults { get; init; } = [];
}

public sealed record ProductConversionContext(
    string ProductFamily,
    string Material,
    string ProductName,
    string CanonicalProductName,
    string IesPath,
    string PdfPath);

public sealed record ProductConversionLog(ProductConversionContext Context, LdtConversionResult Result);

public static class SignifyScoring
{
    public static int Score(ProductRecord record, SignifySearchCandidate candidate)
    {
        var material = Normalize(record.Material);
        var description = Normalize(record.MaterialDescription);
        var sku = Normalize(candidate.Sku);
        var name = Normalize(candidate.Name);
        var displayed = Normalize(candidate.DisplayedDescription);
        var score = 0;
        if (material.Length > 0 && (material == sku || candidate.ProductCodes.Any(code => Normalize(code) == material))) score += 1000;
        if (description.Length > 0 && (description == name || description == displayed)) score += 900;
        if (description.Length > 0 && (name.Contains(description, StringComparison.Ordinal) || displayed.Contains(description, StringComparison.Ordinal))) score += 500;
        var first = description.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (first is not null && (name.StartsWith(first, StringComparison.Ordinal) || displayed.StartsWith(first, StringComparison.Ordinal))) score += 150;
        if (!string.IsNullOrWhiteSpace(candidate.LeafletUrl)) score += 50;
        return score;
    }

    public static bool IsExact(ProductRecord record, SignifySearchCandidate candidate)
    {
        var material = Normalize(record.Material);
        var sku = Normalize(candidate.Sku);
        if (material.Length > 0 && (material.Equals(sku, StringComparison.Ordinal) || candidate.ProductCodes.Any(code => Normalize(code).Equals(material, StringComparison.Ordinal))))
            return true;

        // Empty descriptions carry no product identity. In particular, do not let two
        // omitted values promote an otherwise unrelated candidate to an exact match.
        var description = Normalize(record.MaterialDescription);
        return description.Length > 0 &&
               (description.Equals(Normalize(candidate.Name), StringComparison.Ordinal) ||
                description.Equals(Normalize(candidate.DisplayedDescription), StringComparison.Ordinal));
    }


    public static IReadOnlyList<SignifySearchCandidate> ParseResults(JsonDocument document)
    {
        if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return [];
        return results.EnumerateArray().Select(Parse).ToArray();
    }

    public static int? NextPage(JsonDocument document, int currentPage)
    {
        JsonElement paging;
        if (document.RootElement.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object &&
            meta.TryGetProperty("page", out var livePage) && livePage.ValueKind == JsonValueKind.Object)
        {
            paging = livePage;
        }
        else if ((!document.RootElement.TryGetProperty("paging", out paging) || paging.ValueKind != JsonValueKind.Object) &&
                 (!document.RootElement.TryGetProperty("pagination", out paging) || paging.ValueKind != JsonValueKind.Object)) return null;
        if (TryInt(paging, "next", out var next) || TryInt(paging, "next_page", out next) || TryInt(paging, "nextPage", out next))
            return next > currentPage ? next : null;
        if ((paging.TryGetProperty("next", out var nextElement) || paging.TryGetProperty("next_page", out nextElement) || paging.TryGetProperty("nextPage", out nextElement)) &&
            nextElement.ValueKind == JsonValueKind.String && Uri.TryCreate(nextElement.GetString(), UriKind.Absolute, out var nextUri))
        {
            var query = nextUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
            var pageValue = query.Select(part => part.Split('=', 2)).FirstOrDefault(part => part.Length == 2 && part[0].Equals("page", StringComparison.OrdinalIgnoreCase));
            if (pageValue is not null && int.TryParse(Uri.UnescapeDataString(pageValue[1]), out next) && next > currentPage) return next;
        }
        if (TryBool(paging, "has_next", out var hasNext) || TryBool(paging, "hasNext", out hasNext))
            return hasNext ? currentPage + 1 : null;
        if ((TryInt(paging, "page", out var page) || TryInt(paging, "current", out page) || TryInt(paging, "current_page", out page) || TryInt(paging, "currentPage", out page)) &&
            (TryInt(paging, "total_pages", out var pages) || TryInt(paging, "totalPages", out pages) || TryInt(paging, "pages", out pages)))
            return page < pages ? page + 1 : null;
        return null;
    }

    private static SignifySearchCandidate Parse(JsonElement item)
    {
        string Value(params string[] names)
        {
            foreach (var name in names)
            {
                if (!item.TryGetProperty(name, out var property)) continue;
                if (property.ValueKind == JsonValueKind.String) return property.GetString() ?? string.Empty;
                if (property.ValueKind == JsonValueKind.Object && property.TryGetProperty("value", out var nested)) return nested.ToString();
            }
            return string.Empty;
        }
        IReadOnlyList<string> Codes()
        {
            if (!item.TryGetProperty("product_codes", out var propertyCodes)) return [];
            if (propertyCodes.ValueKind == JsonValueKind.Object && propertyCodes.TryGetProperty("value", out var nested)) propertyCodes = nested;
            return propertyCodes.ValueKind == JsonValueKind.Array
                ? propertyCodes.EnumerateArray().Select(value => value.ToString()).Where(value => value.Length > 0).ToArray()
                : [];
        }
        return new SignifySearchCandidate { Sku = Value("sku", "material"), Name = Value("name"), DisplayedDescription = Value("displayed_order_code_description"), ProductCodes = Codes(), ProductUrl = Value("product_url", "url"), LeafletUrl = Value("leaflet", "leaflet_url"), IesUrl = Value("ies", "ies_url") };
    }

    private static bool TryInt(JsonElement element, string name, out int value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var property)) return false;
        if (property.ValueKind == JsonValueKind.Number) return property.TryGetInt32(out value);
        return property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private static bool TryBool(JsonElement element, string name, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(name, out var property)) return false;
        if (property.ValueKind is JsonValueKind.True or JsonValueKind.False) { value = property.GetBoolean(); return true; }
        return property.ValueKind == JsonValueKind.String && bool.TryParse(property.GetString(), out value);
    }

    private static string Normalize(string? value) => string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
}
