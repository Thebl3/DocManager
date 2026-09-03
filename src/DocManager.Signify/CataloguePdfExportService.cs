using DocManager.Core;

namespace DocManager.Signify;

public enum CataloguePdfExportStatus
{
    Copied,
    Skipped,
    Missing,
    Error
}

public sealed record CataloguePdfExportItem(
    string Code,
    CataloguePdfExportStatus Status,
    string Message,
    string? SourcePath = null,
    string? DestinationPath = null);

public sealed record CataloguePdfExportResult(
    string DestinationDirectory,
    IReadOnlyList<CataloguePdfExportItem> Items)
{
    public int Copied => Items.Count(item => item.Status == CataloguePdfExportStatus.Copied);
    public int Skipped => Items.Count(item => item.Status == CataloguePdfExportStatus.Skipped);
    public int Missing => Items.Count(item => item.Status == CataloguePdfExportStatus.Missing);
    public int Errors => Items.Count(item => item.Status == CataloguePdfExportStatus.Error);

    public string Summary =>
        $"Hoàn tất xuất PDF vào '{DestinationDirectory}': đã sao chép {Copied}; bỏ qua {Skipped}; thiếu {Missing}; lỗi {Errors}.";
}

/// <summary>
/// Resolves each requested product with <see cref="CataloguePdfResolver"/> and safely
/// copies the exact catalogue PDF into a user-selected export folder.
/// </summary>
public sealed class CataloguePdfExportService
{
    public const string DestinationDirectoryName = "Catalouge đèn";

    public CataloguePdfExportResult Export(
        IEnumerable<string> codes,
        IEnumerable<ProductRecord> records,
        string catalogueRoot,
        string destinationParent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        var resolver = new CataloguePdfResolver(records);
        var result = new CatalogueAssetExportCoordinator().Export(
            codes,
            catalogueRoot,
            destinationParent,
            new CatalogueAssetExportOptions(DestinationDirectoryName, "PDF"),
            (code, root, token) => Resolve(resolver, code, root, token),
            cancellationToken);

        return new CataloguePdfExportResult(
            result.DestinationDirectory,
            result.Items.Select(item => new CataloguePdfExportItem(
                item.Code,
                MapStatus(item.Status),
                item.Message,
                item.SourcePath,
                item.DestinationPath)).ToArray());
    }

    private static CatalogueAssetResolution Resolve(CataloguePdfResolver resolver, string code, string root, CancellationToken cancellationToken)
    {
        var resolution = resolver.Resolve(code, root, cancellationToken);
        if (resolution.IsFound && resolution.PdfPath is not null)
            return CatalogueAssetResolution.Found(resolution.PdfPath, resolution.Source ?? "exact");
        return IsMissing(resolution.Status)
            ? CatalogueAssetResolution.Missing(resolution.Message)
            : CatalogueAssetResolution.Error(resolution.Message);
    }

    private static bool IsMissing(CataloguePdfResolutionStatus status) =>
        status is CataloguePdfResolutionStatus.MissingCode or
            CataloguePdfResolutionStatus.NoExactPricelistMatch or
            CataloguePdfResolutionStatus.MissingPdf;

    private static CataloguePdfExportStatus MapStatus(CatalogueAssetExportStatus status) => status switch
    {
        CatalogueAssetExportStatus.Copied => CataloguePdfExportStatus.Copied,
        CatalogueAssetExportStatus.Skipped => CataloguePdfExportStatus.Skipped,
        CatalogueAssetExportStatus.Missing => CataloguePdfExportStatus.Missing,
        _ => CataloguePdfExportStatus.Error
    };
}
