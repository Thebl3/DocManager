using System.Security;
using System.Text.Json;

namespace DocManager.Core;

public sealed record UserSettingsData
{
    public string CatalogueRoot { get; init; } = string.Empty;
    public string PricelistPath { get; init; } = string.Empty;
    public string LastExcelOutputDirectory { get; init; } = string.Empty;
    public string LastCadExcelDirectory { get; init; } = string.Empty;
    public string ExistingFormExcelPath { get; init; } = string.Empty;
    public string LastPdfInputDirectory { get; init; } = string.Empty;
    public string LastLdtDirectory { get; init; } = string.Empty;
    public string LastIesDirectory { get; init; } = string.Empty;
    public string LastPdfCatalogueDirectory { get; init; } = string.Empty;
    public string PendingCodes { get; init; } = string.Empty;
    public bool MergeReports { get; init; } = false;
    public bool? AutoUpdateCadSymbols { get; init; } = null;
    public bool? DownloadIes { get; init; } = null;
    public string? LastFloatingImagePath { get; init; } = null;
    public double FloatingImageOpacityPercent { get; init; } = 100.0;
    public bool FloatingImageTopmost { get; init; } = true;
    public bool FloatingImageClickThrough { get; init; } = false;
    public double? FloatingImageLeft { get; init; } = null;
    public double? FloatingImageTop { get; init; } = null;
    public double? FloatingImageWidth { get; init; } = null;
    public double? FloatingImageHeight { get; init; } = null;
}

public sealed class UserSettingsStore
{
    public const string CanonicalPricelistFileName = "Prof Pricelist V1.0 2026 effective_Mar2026.xlsx";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _sync = new();
    private readonly string _baseDirectory;

    public UserSettingsStore(string? baseDirectory = null, string? localApplicationDataDirectory = null, string? path = null)
    {
        _baseDirectory = PortableAppPaths.ResolveRoot(NormalizeRequiredDirectory(baseDirectory ?? AppContext.BaseDirectory));
        var localRoot = localApplicationDataDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Path = path ?? System.IO.Path.Combine(localRoot, "DocManager", "user-settings.json");
    }

    public string Path { get; }

    public string AdjacentProductFamilyRoot => System.IO.Path.Combine(_baseDirectory, "Product Family");

    public UserSettingsData LoadResolved()
    {
        lock (_sync)
        {
            UserSettingsData? saved = null;
            try
            {
                if (File.Exists(Path))
                    saved = JsonSerializer.Deserialize<UserSettingsData>(File.ReadAllText(Path), JsonOptions);
            }
            catch (Exception exception) when (IsOptionalSettingsException(exception))
            {
                saved = null;
            }

            return Resolve(saved);
        }
    }

    public UserSettingsData NormalizeForSave(UserSettingsData settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var pendingCodes = settings.PendingCodes ?? string.Empty;
        if (pendingCodes.Length > 100_000)
        {
            pendingCodes = pendingCodes.Substring(0, 100_000);
        }

        var opacityPercent = Math.Max(10.0, Math.Min(100.0, settings.FloatingImageOpacityPercent));
        var (floatingImageLeft, floatingImageTop, floatingImageWidth, floatingImageHeight) =
            ValidateFloatingImageBounds(settings.FloatingImageLeft, settings.FloatingImageTop,
                                       settings.FloatingImageWidth, settings.FloatingImageHeight);

        return new UserSettingsData
        {
            CatalogueRoot = NormalizePath(settings.CatalogueRoot) ?? AdjacentProductFamilyRoot,
            PricelistPath = NormalizeExistingFile(settings.PricelistPath, ".xlsx"),
            LastExcelOutputDirectory = NormalizeExistingDirectory(settings.LastExcelOutputDirectory),
            LastCadExcelDirectory = NormalizeExistingDirectory(settings.LastCadExcelDirectory),
            ExistingFormExcelPath = NormalizeExistingFile(settings.ExistingFormExcelPath, ".xlsx"),
            LastPdfInputDirectory = NormalizeExistingDirectory(settings.LastPdfInputDirectory),
            LastLdtDirectory = NormalizeExistingDirectory(settings.LastLdtDirectory),
            LastIesDirectory = NormalizeExistingDirectory(settings.LastIesDirectory),
            LastPdfCatalogueDirectory = NormalizeExistingDirectory(settings.LastPdfCatalogueDirectory),
            PendingCodes = pendingCodes,
            MergeReports = settings.MergeReports,
            AutoUpdateCadSymbols = settings.AutoUpdateCadSymbols ?? true,
            DownloadIes = settings.DownloadIes ?? true,
            LastFloatingImagePath = NormalizeExistingFile(settings.LastFloatingImagePath, ".png") is { Length: > 0 } png ? png :
                                    NormalizeExistingFile(settings.LastFloatingImagePath, ".jpg") is { Length: > 0 } jpg ? jpg :
                                    NormalizeExistingFile(settings.LastFloatingImagePath, ".jpeg") is { Length: > 0 } jpeg ? jpeg :
                                    NormalizeExistingFile(settings.LastFloatingImagePath, ".bmp") is { Length: > 0 } bmp ? bmp :
                                    NormalizeExistingFile(settings.LastFloatingImagePath, ".gif") is { Length: > 0 } gif ? gif :
                                    NormalizeExistingFile(settings.LastFloatingImagePath, ".tif") is { Length: > 0 } tif ? tif :
                                    NormalizeExistingFile(settings.LastFloatingImagePath, ".tiff") is { Length: > 0 } tiff ? tiff :
                                    NormalizeExistingFile(settings.LastFloatingImagePath, ".webp") is { Length: > 0 } webp ? webp :
                                    null,
            FloatingImageOpacityPercent = opacityPercent,
            FloatingImageTopmost = settings.FloatingImageTopmost,
            FloatingImageClickThrough = settings.FloatingImageClickThrough,
            FloatingImageLeft = floatingImageLeft,
            FloatingImageTop = floatingImageTop,
            FloatingImageWidth = floatingImageWidth,
            FloatingImageHeight = floatingImageHeight
        };
    }

    public void Save(UserSettingsData settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_sync)
        {
            string? temporaryPath = null;
            try
            {
                var normalized = NormalizeForSave(settings);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
                temporaryPath = AtomicFile.CreateSiblingTemporaryPath(Path);
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(normalized, JsonOptions));
                AtomicFile.Replace(temporaryPath, Path);
                temporaryPath = null;
            }
            catch (Exception exception) when (IsOptionalSettingsException(exception))
            {
                // User preferences are optional and must never prevent the application from continuing or closing.
            }
            finally
            {
                if (temporaryPath is not null)
                {
                    try { File.Delete(temporaryPath); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException) { }
                }
            }
        }
    }

    public static bool TryNormalizePath(string? value, out string normalized)
    {
        normalized = string.Empty;
        var candidate = NormalizePath(value);
        if (candidate is null) return false;
        normalized = candidate;
        return true;
    }

    private UserSettingsData Resolve(UserSettingsData? saved)
    {
        var savedRoot = NormalizeExistingDirectory(saved?.CatalogueRoot);
        var catalogueRoot = string.IsNullOrEmpty(savedRoot) ? AdjacentProductFamilyRoot : savedRoot;
        var savedPricelist = NormalizeExistingFile(saved?.PricelistPath, ".xlsx");
        var savedImagePath = ResolveImagePath(saved?.LastFloatingImagePath);
        var opacityPercent = Math.Max(10.0, Math.Min(100.0, saved?.FloatingImageOpacityPercent ?? 100.0));
        var (floatingImageLeft, floatingImageTop, floatingImageWidth, floatingImageHeight) =
            ValidateFloatingImageBounds(saved?.FloatingImageLeft, saved?.FloatingImageTop,
                                       saved?.FloatingImageWidth, saved?.FloatingImageHeight);

        return new UserSettingsData
        {
            CatalogueRoot = catalogueRoot,
            PricelistPath = string.IsNullOrEmpty(savedPricelist) ? DiscoverPricelist(catalogueRoot) : savedPricelist,
            LastExcelOutputDirectory = NormalizeExistingDirectory(saved?.LastExcelOutputDirectory),
            LastCadExcelDirectory = NormalizeExistingDirectory(saved?.LastCadExcelDirectory),
            ExistingFormExcelPath = NormalizeExistingFile(saved?.ExistingFormExcelPath, ".xlsx"),
            LastPdfInputDirectory = NormalizeExistingDirectory(saved?.LastPdfInputDirectory),
            LastLdtDirectory = NormalizeExistingDirectory(saved?.LastLdtDirectory),
            LastIesDirectory = NormalizeExistingDirectory(saved?.LastIesDirectory),
            LastPdfCatalogueDirectory = NormalizeExistingDirectory(saved?.LastPdfCatalogueDirectory),
            PendingCodes = saved?.PendingCodes ?? string.Empty,
            MergeReports = saved?.MergeReports ?? false,
            AutoUpdateCadSymbols = saved?.AutoUpdateCadSymbols ?? true,
            DownloadIes = saved?.DownloadIes ?? true,
            LastFloatingImagePath = savedImagePath,
            FloatingImageOpacityPercent = opacityPercent,
            FloatingImageTopmost = saved?.FloatingImageTopmost ?? true,
            FloatingImageClickThrough = saved?.FloatingImageClickThrough ?? false,
            FloatingImageLeft = floatingImageLeft,
            FloatingImageTop = floatingImageTop,
            FloatingImageWidth = floatingImageWidth,
            FloatingImageHeight = floatingImageHeight
        };
    }

    private static string? ResolveImagePath(string? path)
    {
        var extensions = new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp" };
        foreach (var ext in extensions)
        {
            var resolved = NormalizeExistingFile(path, ext);
            if (!string.IsNullOrEmpty(resolved)) return resolved;
        }
        return null;
    }

    private string DiscoverPricelist(string catalogueRoot)
    {
        var directories = new[]
            {
                System.IO.Path.Combine(_baseDirectory, "Product excel file"),
                _baseDirectory,
                System.IO.Path.GetDirectoryName(catalogueRoot)
            }
            .Where(directory => !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            .Select(directory => System.IO.Path.GetFullPath(directory!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var directory in directories)
        {
            var exact = System.IO.Path.Combine(directory, CanonicalPricelistFileName);
            if (File.Exists(exact)) return System.IO.Path.GetFullPath(exact);
        }

        foreach (var directory in directories)
        {
            try
            {
                var candidate = Directory.EnumerateFiles(directory, "Prof Pricelist*.xlsx", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (candidate is not null) return System.IO.Path.GetFullPath(candidate);
            }
            catch (Exception exception) when (IsOptionalSettingsException(exception))
            {
                // Continue with the next portable candidate directory.
            }
        }

        return string.Empty;
    }

    private static string NormalizeRequiredDirectory(string value)
    {
        if (TryNormalizePath(value, out var normalized)) return normalized;
        throw new ArgumentException("Base directory is invalid.", nameof(value));
    }

    private static string NormalizeExistingDirectory(string? value)
    {
        var normalized = NormalizePath(value);
        return normalized is not null && Directory.Exists(normalized) ? normalized : string.Empty;
    }

    private static string NormalizeExistingFile(string? value, string requiredExtension)
    {
        var normalized = NormalizePath(value);
        return normalized is not null &&
               File.Exists(normalized) &&
               System.IO.Path.GetExtension(normalized).Equals(requiredExtension, StringComparison.OrdinalIgnoreCase)
            ? normalized
            : string.Empty;
    }

    private static string? NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return System.IO.Path.GetFullPath(value.Trim()); }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or SecurityException) { return null; }
    }

    private static bool IsOptionalSettingsException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException or SecurityException;

    private static (double?, double?, double?, double?) ValidateFloatingImageBounds(
        double? left, double? top, double? width, double? height)
    {
        var isLeftValid = left.HasValue && double.IsFinite(left.Value);
        var isTopValid = top.HasValue && double.IsFinite(top.Value);
        var isWidthValid = width.HasValue && double.IsFinite(width.Value) && width.Value > 0;
        var isHeightValid = height.HasValue && double.IsFinite(height.Value) && height.Value > 0;

        if (!isLeftValid || !isTopValid || !isWidthValid || !isHeightValid)
            return (null, null, null, null);

        return (left, top, width, height);
    }
}
