using DocManager.Core;

namespace DocManager.Photometry;

public enum IesCataloguePdfResolutionStatus
{
    Found,
    NotFound,
    Ambiguous,
    UnsafePath,
    InvalidSource,
    ScanFailed
}

public sealed record IesCataloguePdfResolution(
    IesCataloguePdfResolutionStatus Status,
    string Message,
    string? PdfPath = null,
    bool IsLegacy = false,
    string? Source = null)
{
    public bool IsFound => Status == IesCataloguePdfResolutionStatus.Found;
}

/// <summary>
/// Conservatively finds catalogue PDFs only beside the IES or in its family
/// directory. It never searches fuzzy siblings or leaves the allowed family root.
/// </summary>
public static class IesCataloguePdfResolver
{
    public static IesCataloguePdfResolution Resolve(
        string iesPath,
        string canonicalProductName,
        string? manifestPdfPath = null,
        string? allowedRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iesPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalProductName);

        string source;
        string sourceDirectory;
        string familyDirectory;
        string root;
        try
        {
            source = Path.GetFullPath(iesPath);
            sourceDirectory = Path.GetDirectoryName(source) ?? throw new InvalidDataException("IES không có thư mục nguồn.");
            familyDirectory = Path.GetFileName(sourceDirectory).Equals("IES", StringComparison.OrdinalIgnoreCase)
                ? Directory.GetParent(sourceDirectory)?.FullName ?? sourceDirectory
                : sourceDirectory;
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(allowedRoot ?? familyDirectory));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or PathTooLongException)
        {
            return Result(IesCataloguePdfResolutionStatus.InvalidSource, $"Đường dẫn IES không hợp lệ: {exception.Message}");
        }

        if (!FileSystemSafety.IsSafePathWithinDirectory(source, root) ||
            !FileSystemSafety.IsSafePathWithinDirectory(sourceDirectory, root) ||
            !FileSystemSafety.IsSafePathWithinDirectory(familyDirectory, root))
            return Result(IesCataloguePdfResolutionStatus.UnsafePath, "IES hoặc thư mục catalogue nằm ngoài root được phép hoặc qua symbolic link, junction, reparse point.");

        if (!File.Exists(source))
            return Result(IesCataloguePdfResolutionStatus.InvalidSource, "IES nguồn không tồn tại.");

        var canonical = ProductAssetName.CanonicalBaseName(canonicalProductName);
        if (!string.IsNullOrWhiteSpace(manifestPdfPath))
        {
            string manifest;
            try { manifest = Path.GetFullPath(manifestPdfPath); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Result(IesCataloguePdfResolutionStatus.UnsafePath, $"Đường dẫn PDF manifest không hợp lệ: {exception.Message}");
            }

            if (!IsSafePdf(manifest, root) || !IsExpectedIdentity(manifest, canonical))
                return Result(IesCataloguePdfResolutionStatus.UnsafePath, "PDF manifest nằm ngoài root hoặc không đúng identity sản phẩm.");
            return Result(IesCataloguePdfResolutionStatus.Found, $"Đã tìm PDF theo manifest: {Path.GetFileName(manifest)}", manifest, IsLegacy(manifest, canonical), "manifest");
        }

        string[] candidates;
        try
        {
            var directories = new[] { sourceDirectory, familyDirectory }
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(directory => FileSystemSafety.IsSafePathWithinDirectory(directory, root) && Directory.Exists(directory));
            candidates = directories.SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                .Where(path => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFullPath)
                .Where(path => IsSafePdf(path, root))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return Result(IesCataloguePdfResolutionStatus.ScanFailed, $"Không thể quét catalogue PDF: {exception.Message}");
        }

        var exact = candidates.Where(path => Path.GetFileNameWithoutExtension(path).Equals(canonical, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (exact.Length == 1)
            return Result(IesCataloguePdfResolutionStatus.Found, $"Đã tìm PDF canonical: {Path.GetFileName(exact[0])}", exact[0], source: "canonical");
        if (exact.Length > 1)
            return Result(IesCataloguePdfResolutionStatus.Ambiguous, $"Có nhiều PDF canonical khớp '{canonical}'.");

        var legacy = candidates.Where(path => IsLegacy(path, canonical)).ToArray();
        return legacy.Length switch
        {
            1 => Result(IesCataloguePdfResolutionStatus.Found, $"Đã tìm PDF legacy duy nhất: {Path.GetFileName(legacy[0])}", legacy[0], true, "legacy-sku"),
            > 1 => Result(IesCataloguePdfResolutionStatus.Ambiguous, $"Có nhiều PDF legacy khớp '{canonical}'."),
            _ => Result(IesCataloguePdfResolutionStatus.NotFound, $"Không tìm thấy PDF catalogue chính xác cho '{canonical}'.")
        };
    }

    private static bool IsExpectedIdentity(string path, string canonical) =>
        Path.GetFileNameWithoutExtension(path).Equals(canonical, StringComparison.OrdinalIgnoreCase) || IsLegacy(path, canonical);

    private static bool IsLegacy(string path, string canonical)
    {
        var stem = Path.GetFileNameWithoutExtension(path).Trim();
        var separator = stem.IndexOf(" - ", StringComparison.Ordinal);
        return separator is >= 3 and <= 18 &&
               stem[..separator].All(char.IsDigit) &&
               ProductAssetName.CanonicalBaseName(stem[(separator + 3)..]).Equals(canonical, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafePdf(string path, string root) =>
        Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase) &&
        File.Exists(path) &&
        FileSystemSafety.IsSafePathWithinDirectory(path, root);

    private static IesCataloguePdfResolution Result(
        IesCataloguePdfResolutionStatus status,
        string message,
        string? path = null,
        bool isLegacy = false,
        string? source = null) => new(status, message, path, isLegacy, source);
}
