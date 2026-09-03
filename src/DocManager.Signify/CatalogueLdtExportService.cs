using DocManager.Core;

namespace DocManager.Signify;

public enum CatalogueLdtExportStatus
{
    Copied,
    Skipped,
    Missing,
    Error
}

public sealed record CatalogueLdtExportItem(
    string Code,
    CatalogueLdtExportStatus Status,
    string Message,
    string? SourcePath = null,
    string? DestinationPath = null);

public sealed record CatalogueLdtExportResult(
    string DestinationDirectory,
    IReadOnlyList<CatalogueLdtExportItem> Items)
{
    public int Copied => Items.Count(item => item.Status == CatalogueLdtExportStatus.Copied);
    public int Skipped => Items.Count(item => item.Status == CatalogueLdtExportStatus.Skipped);
    public int Missing => Items.Count(item => item.Status == CatalogueLdtExportStatus.Missing);
    public int Errors => Items.Count(item => item.Status == CatalogueLdtExportStatus.Error);

    public string Summary =>
        $"Hoàn tất xuất LDT vào '{DestinationDirectory}': đã sao chép {Copied}; bỏ qua {Skipped}; thiếu {Missing}; lỗi {Errors}.";
}

/// <summary>
/// Resolves requested products inside their pricelist family and copies only exact
/// canonical or conservative legacy LDT identities into a user-selected export folder.
/// </summary>
public sealed class CatalogueLdtExportService
{
    public const string DestinationDirectoryName = "File đèn";

    public CatalogueLdtExportResult Export(
        IEnumerable<string> codes,
        IEnumerable<ProductRecord> records,
        string catalogueRoot,
        string destinationParent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        var pricelist = records.ToArray();
        var result = new CatalogueAssetExportCoordinator().Export(
            codes,
            catalogueRoot,
            destinationParent,
            new CatalogueAssetExportOptions(DestinationDirectoryName, "LDT"),
            (code, root, token) => Resolve(pricelist, code, root, token),
            cancellationToken);

        return new CatalogueLdtExportResult(
            result.DestinationDirectory,
            result.Items.Select(item => new CatalogueLdtExportItem(
                item.Code,
                MapStatus(item.Status),
                item.Message,
                item.SourcePath,
                item.DestinationPath)).ToArray());
    }

    private static CatalogueAssetResolution Resolve(
        IReadOnlyList<ProductRecord> records,
        string code,
        string root,
        CancellationToken cancellationToken)
    {
        var product = CatalogueProductResolver.Resolve(records, code);
        if (product.Status == CatalogueProductResolutionStatus.Ambiguous)
            return CatalogueAssetResolution.Error("Mã/tên khớp nhiều sản phẩm trong pricelist; không chọn variant bất kỳ.");
        if (product.Record is null)
            return CatalogueAssetResolution.Missing("Không tìm thấy mã/tên khớp an toàn trong pricelist.");

        var source = ResolveSource(new CanonicalAssetManifest(root), root, product.Record, cancellationToken);
        return source.Path is not null
            ? CatalogueAssetResolution.Found(source.Path, source.Kind)
            : source.HasCollision
                ? CatalogueAssetResolution.Error(source.Message)
                : CatalogueAssetResolution.Missing(source.Message);
    }

    private static SourceResolution ResolveSource(
        CanonicalAssetManifest manifest,
        string root,
        ProductRecord record,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var family = Path.GetFullPath(Path.Combine(root, WindowsFileName.Sanitize(record.ProductFamily)));
        if (!FileSystemSafety.IsPathWithinDirectory(family, root))
            return new SourceResolution(null, false, "Product Family trong pricelist không hợp lệ.", string.Empty);
        if (Directory.Exists(family) && !FileSystemSafety.HasNoReparsePointsInExistingPath(family))
            return new SourceResolution(null, true, "Product Family chứa symbolic link, junction hoặc reparse point; không xuất LDT.", string.Empty);

        var lookup = manifest.Resolve(record);
        if (lookup.HasCollision)
            return new SourceResolution(null, true, "Manifest canonical có xung đột identity; không xuất LDT.", string.Empty);

        if (lookup.Entry is not null)
        {
            var manifestPath = manifest.ExistingPath(lookup.Entry, ".ldt");
            if (manifestPath is not null)
                return IsDirectLdt(manifestPath, Path.Combine(family, "LDT"))
                    ? new SourceResolution(manifestPath, false, string.Empty, "manifest")
                    : new SourceResolution(null, true, "LDT trong manifest không nằm trong đúng Product Family/LDT.", string.Empty);
        }

        var ldtDirectory = Path.Combine(family, "LDT");
        if (!Directory.Exists(ldtDirectory))
            return new SourceResolution(null, false, $"Không tìm thấy thư mục LDT của '{record.ProductFamily}'.", string.Empty);
        if (!FileSystemSafety.HasNoReparsePointsInExistingPath(ldtDirectory))
            return new SourceResolution(null, true, "Thư mục LDT chứa symbolic link, junction hoặc reparse point; không xuất LDT.", string.Empty);

        var files = Directory.EnumerateFiles(ldtDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path).Equals(".ldt", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFullPath)
            .Where(path => IsDirectLdt(path, ldtDirectory))
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var productName = lookup.Entry?.CanonicalBaseName ?? ProductAssetName.CanonicalBaseName(record.MaterialDescription);
        var canonicalPath = Path.Combine(ldtDirectory, productName + ".ldt");
        var canonical = files.Where(path => path.Equals(canonicalPath, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (canonical.Length == 1) return new SourceResolution(canonical[0], false, string.Empty, "canonical");
        if (canonical.Length > 1)
            return new SourceResolution(null, true, $"Có nhiều LDT canonical khớp identity '{productName}'; không chọn file bất kỳ.", string.Empty);

        var materialKey = TextNormalization.ProductKey(record.Material);
        var legacyName = files.Where(path =>
        {
            if (HasNumericLegacySkuPrefix(path, out var skuPrefix) &&
                (!materialKey.All(char.IsDigit) ||
                 !TextNormalization.ProductKey(skuPrefix).Equals(materialKey, StringComparison.OrdinalIgnoreCase)))
                return false;

            return TextNormalization.ProductKey(ProductAssetName.StripLegacySkuPrefix(Path.GetFileNameWithoutExtension(path)))
                .Equals(TextNormalization.ProductKey(productName), StringComparison.OrdinalIgnoreCase);
        }).ToArray();
        if (legacyName.Length == 1) return new SourceResolution(legacyName[0], false, string.Empty, "legacy");
        if (legacyName.Length > 1)
            return new SourceResolution(null, true, $"Có nhiều LDT legacy khớp identity '{productName}'; không chọn file bất kỳ.", string.Empty);

        return new SourceResolution(null, false, $"Không tìm thấy LDT chính xác cho '{record.MaterialDescription}'.", string.Empty);
    }

    private static bool HasNumericLegacySkuPrefix(string path, out string skuPrefix)
    {
        var stem = Path.GetFileNameWithoutExtension(path).TrimStart();
        var separator = stem.IndexOf(" - ", StringComparison.Ordinal);
        skuPrefix = separator > 0 ? stem[..separator] : string.Empty;
        return skuPrefix.Length > 0 && skuPrefix.All(char.IsDigit);
    }

    private static bool IsDirectLdt(string path, string directory) =>
        File.Exists(path) &&
        Path.GetExtension(path).Equals(".ldt", StringComparison.OrdinalIgnoreCase) &&
        FileSystemSafety.IsPathWithinDirectory(path, directory) &&
        FileSystemSafety.HasNoReparsePointsInExistingPath(path);

    private static CatalogueLdtExportStatus MapStatus(CatalogueAssetExportStatus status) => status switch
    {
        CatalogueAssetExportStatus.Copied => CatalogueLdtExportStatus.Copied,
        CatalogueAssetExportStatus.Skipped => CatalogueLdtExportStatus.Skipped,
        CatalogueAssetExportStatus.Missing => CatalogueLdtExportStatus.Missing,
        _ => CatalogueLdtExportStatus.Error
    };

    private sealed record SourceResolution(string? Path, bool HasCollision, string Message, string Kind);
}
