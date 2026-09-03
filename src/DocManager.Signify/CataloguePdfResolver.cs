using DocManager.Core;

namespace DocManager.Signify;

public enum CataloguePdfResolutionStatus
{
    Found,
    MissingCode,
    InvalidRoot,
    RootNotFound,
    NoExactPricelistMatch,
    AmbiguousProduct,
    InvalidManifest,
    MissingPdf,
    AmbiguousPdf,
    ScanFailed
}

public sealed record CataloguePdfResolution(
    CataloguePdfResolutionStatus Status,
    string Message,
    string? PdfPath = null,
    ProductRecord? Record = null,
    string? Source = null)
{
    public bool IsFound => Status == CataloguePdfResolutionStatus.Found;
}

/// <summary>
/// Resolves a catalogue PDF for one exact pricelist product without launching it.
/// Only PDF files directly inside that product's family directory are candidates.
/// </summary>
public sealed class CataloguePdfResolver
{
    private readonly IReadOnlyList<ProductRecord> _records;

    public CataloguePdfResolver(IEnumerable<ProductRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        _records = records.ToArray();
    }

    public CataloguePdfResolution Resolve(string? code, string? outputRoot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedCode = TextNormalization.ProductKey(code);
        if (normalizedCode.Length == 0)
            return Result(CataloguePdfResolutionStatus.MissingCode, "Chưa nhập mã để mở PDF.");
        if (string.IsNullOrWhiteSpace(outputRoot))
            return Result(CataloguePdfResolutionStatus.InvalidRoot, "Chưa chọn thư mục Product Family.");

        string root;
        try
        {
            root = Path.GetFullPath(outputRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result(CataloguePdfResolutionStatus.InvalidRoot, $"Đường dẫn Product Family không hợp lệ: {exception.Message}");
        }

        if (!Directory.Exists(root))
            return Result(CataloguePdfResolutionStatus.RootNotFound, "Product Family chưa tồn tại.");
        if (!FileSystemSafety.HasNoReparsePointsInExistingPath(root))
            return Result(CataloguePdfResolutionStatus.InvalidRoot, "Product Family chứa symbolic link, junction hoặc reparse point; từ chối theo đường dẫn này.");

        var product = CatalogueProductResolver.Resolve(_records, code);
        if (product.Status == CatalogueProductResolutionStatus.Missing)
            return Result(CataloguePdfResolutionStatus.NoExactPricelistMatch, $"Không tìm thấy mã/tên khớp an toàn trong pricelist: {code?.Trim()}.");
        if (product.Status == CatalogueProductResolutionStatus.Ambiguous)
            return Result(CataloguePdfResolutionStatus.AmbiguousProduct, $"Mã/tên '{code?.Trim()}' khớp nhiều sản phẩm; không mở PDF variant bất kỳ.");

        var record = product.Record!;
        var familyName = WindowsFileName.Sanitize(record.ProductFamily);
        var family = Path.GetFullPath(Path.Combine(root, familyName));
        if (!FileSystemSafety.IsPathWithinDirectory(family, root))
            return Result(CataloguePdfResolutionStatus.InvalidRoot, "Product Family trong pricelist không hợp lệ.", record: record);
        if (Directory.Exists(family) && !FileSystemSafety.HasNoReparsePointsInExistingPath(family))
            return Result(CataloguePdfResolutionStatus.InvalidRoot, "Product Family chứa symbolic link, junction hoặc reparse point; từ chối PDF.", record: record);

        CanonicalAssetManifestLookup manifestLookup;
        var manifest = new CanonicalAssetManifest(root);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            manifestLookup = manifest.Resolve(record);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return Result(CataloguePdfResolutionStatus.InvalidManifest, $"Không dùng manifest canonical: {exception.Message}", record: record);
        }

        if (manifestLookup.HasCollision)
            return Result(CataloguePdfResolutionStatus.AmbiguousPdf, "Manifest canonical có xung đột identity; không mở PDF.", record: record);

        var names = ProductNames(record, manifestLookup.Entry);
        if (manifestLookup.Entry is not null)
        {
            try
            {
                var manifestPdf = manifest.ExistingPath(manifestLookup.Entry, ".pdf");
                if (manifestPdf is not null)
                {
                    if (!IsSafePdf(manifestPdf, family))
                        return Result(CataloguePdfResolutionStatus.InvalidManifest, "Đường dẫn PDF trong manifest không nằm trong đúng Product Family.", record: record);
                    return Result(CataloguePdfResolutionStatus.Found, $"Đã resolve PDF theo manifest: {Path.GetFileName(manifestPdf)}", manifestPdf, record, "manifest");
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                return Result(CataloguePdfResolutionStatus.InvalidManifest, $"Không dùng PDF trong manifest: {exception.Message}", record: record);
            }
        }

        if (!Directory.Exists(family))
            return Result(CataloguePdfResolutionStatus.MissingPdf, $"Không tìm thấy Product Family '{familyName}' cho {record.MaterialDescription}.", record: record);

        string[] pdfs;
        try
        {
            pdfs = Directory.EnumerateFiles(family, "*", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFullPath)
                .Where(path => IsSafePdf(path, family))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result(CataloguePdfResolutionStatus.ScanFailed, $"Không thể quét PDF trong Product Family: {exception.Message}", record: record);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var canonical = pdfs.Where(path => names.Contains(Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)).ToArray();
        if (canonical.Length == 1)
            return Result(CataloguePdfResolutionStatus.Found, $"Đã resolve PDF canonical: {Path.GetFileName(canonical[0])}", canonical[0], record, "canonical");
        if (canonical.Length > 1)
            return Result(CataloguePdfResolutionStatus.AmbiguousPdf, $"Có nhiều PDF canonical khớp {record.MaterialDescription}; không mở variant bất kỳ.", record: record);

        var numericMaterial = record.Material.Trim();
        if (numericMaterial.Length > 0 && numericMaterial.All(char.IsDigit))
        {
            var legacySku = pdfs.Where(path => HasExactLegacySkuPrefix(path, numericMaterial)).ToArray();
            if (legacySku.Length > 1)
                return Result(CataloguePdfResolutionStatus.AmbiguousPdf, $"Material {numericMaterial} có nhiều PDF legacy; không mở variant bất kỳ.", record: record);
            if (legacySku.Length == 1 && names.Contains(ProductAssetName.StripLegacySkuPrefix(Path.GetFileNameWithoutExtension(legacySku[0])), StringComparer.OrdinalIgnoreCase))
                return Result(CataloguePdfResolutionStatus.Found, $"Đã resolve PDF legacy theo Material: {Path.GetFileName(legacySku[0])}", legacySku[0], record, "legacy-sku");
        }

        var nameKeys = names.Select(TextNormalization.ProductKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var legacyName = pdfs.Where(path =>
                !HasNumericLegacySkuPrefix(path) &&
                nameKeys.Contains(TextNormalization.ProductKey(Path.GetFileNameWithoutExtension(path))))
            .ToArray();
        if (legacyName.Length == 1)
            return Result(CataloguePdfResolutionStatus.Found, $"Đã resolve PDF legacy theo tên: {Path.GetFileName(legacyName[0])}", legacyName[0], record, "legacy-name");
        if (legacyName.Length > 1)
            return Result(CataloguePdfResolutionStatus.AmbiguousPdf, $"Có nhiều PDF legacy khớp {record.MaterialDescription}; không mở variant bất kỳ.", record: record);

        return Result(CataloguePdfResolutionStatus.MissingPdf, $"Không tìm thấy PDF catalogue chính xác cho {record.MaterialDescription} trong '{familyName}'.", record: record);
    }

    private static string[] ProductNames(ProductRecord record, CanonicalAssetManifestEntry? entry) =>
        new[]
        {
            entry?.CanonicalBaseName,
            ProductAssetName.CanonicalBaseName(record.MaterialDescription)
        }
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray()!;

    private static bool HasExactLegacySkuPrefix(string path, string material)
    {
        var stem = Path.GetFileNameWithoutExtension(path).TrimStart();
        var separator = stem.IndexOf(" - ", StringComparison.Ordinal);
        return separator > 0 &&
               stem[..separator].All(char.IsDigit) &&
               stem[..separator].Equals(material, StringComparison.Ordinal);
    }

    private static bool HasNumericLegacySkuPrefix(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path).TrimStart();
        var separator = stem.IndexOf(" - ", StringComparison.Ordinal);
        return separator > 0 && stem[..separator].All(char.IsDigit);
    }

    private static bool IsSafePdf(string path, string family) =>
        Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase) &&
        File.Exists(path) &&
        FileSystemSafety.IsPathWithinDirectory(path, family) &&
        FileSystemSafety.HasNoReparsePointsInExistingPath(path);

    private static CataloguePdfResolution Result(
        CataloguePdfResolutionStatus status,
        string message,
        string? pdfPath = null,
        ProductRecord? record = null,
        string? source = null) => new(status, message, pdfPath, record, source);
}
