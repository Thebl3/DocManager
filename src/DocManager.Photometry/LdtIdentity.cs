namespace DocManager.Photometry;

public static class LdtIdentity
{
    public static string SourceStem(string iesPath) => Path.GetFileNameWithoutExtension(iesPath).Trim();

    public static bool IsIdentityMatch(string iesPath, string ldtPath) =>
        IsIdentityMatch(SourceStem(iesPath), iesPath, ldtPath);

    public static bool IsIdentityMatch(string canonicalProductName, string iesPath, string ldtPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iesPath);
        var canonical = DocManager.Core.ProductAssetName.CanonicalBaseName(canonicalProductName);
        var candidate = Path.GetFileNameWithoutExtension(ldtPath);
        if (candidate.Equals(canonical, StringComparison.OrdinalIgnoreCase)) return true;

        // Historical converter outputs remain discoverable when their name is an
        // exact product identity after SKU-prefix removal and punctuation folding.
        var legacy = DocManager.Core.ProductAssetName.Resolve([ldtPath], canonicalProductName, allowGeneratedLdtSuffix: true);
        return legacy.Path is not null;
    }

    public static string ChooseOutputPathFromIes(string iesPath, string outputDirectory) =>
        ChooseOutputPath(DocManager.Core.ProductAssetName.StripLegacySkuPrefix(SourceStem(iesPath)), outputDirectory);

    public static string ChooseOutputPath(string canonicalProductName, string outputDirectory)
    {
        var exact = Path.Combine(outputDirectory, $"{DocManager.Core.ProductAssetName.CanonicalBaseName(canonicalProductName)}.ldt");
        if (!File.Exists(exact)) return exact;
        throw new IOException($"LDT canonical đã tồn tại: {exact}");
    }

    public static string? FindExisting(string iesPath, string ldtDirectory, Func<string, string?>? legacyHeaderReader = null) =>
        FindExisting(SourceStem(iesPath), iesPath, ldtDirectory, legacyHeaderReader);

    public static string? FindExisting(string canonicalProductName, string iesPath, string ldtDirectory, Func<string, string?>? legacyHeaderReader = null, bool allowGeneratedLdtSuffix = true)
    {
        if (!Directory.Exists(ldtDirectory)) return null;
        var files = Directory.EnumerateFiles(ldtDirectory, "*.ldt", SearchOption.TopDirectoryOnly)
            .Where(path => allowGeneratedLdtSuffix || !DocManager.Core.ProductAssetName.IsGeneratedLdtStem(Path.GetFileNameWithoutExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var resolution = DocManager.Core.ProductAssetName.Resolve(files, canonicalProductName, allowGeneratedLdtSuffix);
        if (resolution.HasCollision) throw new InvalidDataException($"name_collision: nhiều LDT khớp '{canonicalProductName}'.");
        if (resolution.Path is not null) return resolution.Path;
        if (legacyHeaderReader is null) return null;
        string? sourceHeader;
        try { sourceHeader = NormalizeHeader(legacyHeaderReader(iesPath)); }
        catch { return null; }
        if (string.IsNullOrWhiteSpace(sourceHeader)) return null;

        var siblings = Directory.EnumerateFiles(Path.GetDirectoryName(iesPath)!, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path).Equals(".ies", StringComparison.OrdinalIgnoreCase)).ToArray();
        var matching = 0;
        foreach (var sibling in siblings)
        {
            string? header;
            try { header = NormalizeHeader(legacyHeaderReader(sibling)); }
            catch { return null; }
            if (string.Equals(header, sourceHeader, StringComparison.OrdinalIgnoreCase)) matching++;
        }
        if (matching != 1) return null;
        var legacy = files.Where(path =>
        {
            try { return string.Equals(NormalizeHeader(legacyHeaderReader(path)), sourceHeader, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }).ToArray();
        return legacy.Length == 1 ? legacy[0] : null;
    }

    private static string NormalizeHeader(string? value) => DocManager.Core.TextNormalization.ProductKey(value);
}
