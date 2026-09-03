using System.Security;
using System.Security.Cryptography;
using DocManager.Core;

namespace DocManager.Signify;

internal enum CatalogueAssetExportStatus
{
    Copied,
    Skipped,
    Missing,
    Error
}

internal sealed record CatalogueAssetExportItem(
    string Code,
    CatalogueAssetExportStatus Status,
    string Message,
    string? SourcePath = null,
    string? DestinationPath = null);

internal sealed record CatalogueAssetExportResult(
    string DestinationDirectory,
    IReadOnlyList<CatalogueAssetExportItem> Items);

internal enum CatalogueAssetResolutionStatus
{
    Found,
    Missing,
    Error
}

internal sealed record CatalogueAssetResolution(
    CatalogueAssetResolutionStatus Status,
    string Message,
    string? SourcePath = null,
    string? ResolutionKind = null)
{
    public static CatalogueAssetResolution Found(string sourcePath, string resolutionKind) =>
        new(CatalogueAssetResolutionStatus.Found, string.Empty, sourcePath, resolutionKind);

    public static CatalogueAssetResolution Missing(string message) =>
        new(CatalogueAssetResolutionStatus.Missing, message);

    public static CatalogueAssetResolution Error(string message) =>
        new(CatalogueAssetResolutionStatus.Error, message);
}

internal sealed record CatalogueAssetExportOptions(string DestinationDirectoryName, string AssetLabel);

/// <summary>
/// Coordinates safe, non-destructive catalogue asset exports after an asset-specific resolver
/// has identified a source file. File-system publication remains isolated here so it can be
/// replaced with a shared publication helper without changing PDF or LDT resolution rules.
/// </summary>
internal sealed class CatalogueAssetExportCoordinator
{
    private readonly Action? _beforeDestinationDirectoryCreation;
    private readonly Action? _afterDestinationDirectoryCreation;

    internal CatalogueAssetExportCoordinator(
        Action? beforeDestinationDirectoryCreation = null,
        Action? afterDestinationDirectoryCreation = null)
    {
        _beforeDestinationDirectoryCreation = beforeDestinationDirectoryCreation;
        _afterDestinationDirectoryCreation = afterDestinationDirectoryCreation;
    }

    public CatalogueAssetExportResult Export(
        IEnumerable<string> codes,
        string catalogueRoot,
        string destinationParent,
        CatalogueAssetExportOptions options,
        Func<string, string, CancellationToken, CatalogueAssetResolution> resolve,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(codes);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(resolve);

        var root = ExistingDirectory(catalogueRoot, nameof(catalogueRoot));
        var parent = ExistingDirectory(destinationParent, nameof(destinationParent));
        if (!FileSystemSafety.HasNoReparsePointsInExistingPath(root))
            throw new InvalidDataException($"Product Family chứa symbolic link, junction hoặc reparse point; từ chối xuất {options.AssetLabel}.");
        if (!FileSystemSafety.HasNoReparsePointsInExistingPath(parent))
            throw new InvalidDataException($"Thư mục cha đích chứa symbolic link, junction hoặc reparse point; từ chối xuất {options.AssetLabel}.");

        var destinationDirectory = Path.GetFullPath(Path.Combine(parent, options.DestinationDirectoryName));
        if (!FileSystemSafety.IsPathWithinDirectory(destinationDirectory, parent))
            throw new InvalidDataException($"Thư mục {options.DestinationDirectoryName} không nằm trong thư mục cha đã chọn.");
        if (Directory.Exists(destinationDirectory) && !FileSystemSafety.HasNoReparsePointsInExistingPath(destinationDirectory))
            throw new InvalidDataException($"Thư mục {options.DestinationDirectoryName} chứa symbolic link, junction hoặc reparse point; từ chối xuất {options.AssetLabel}.");

        var requestedCodes = codes
            .Select(code => code?.Trim() ?? string.Empty)
            .Where(code => code.Length > 0)
            .ToArray();
        if (requestedCodes.Length == 0)
            return new CatalogueAssetExportResult(destinationDirectory, []);

        var work = ResolveRequests(requestedCodes, root, destinationDirectory, options.AssetLabel, resolve, cancellationToken);
        DeduplicateSources(work);
        RejectDestinationCollisions(work, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        EnsureSafeDestinationDirectory(parent, destinationDirectory, options);

        foreach (var item in work.Where(item => item.Result is null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Publish(item, cancellationToken);
        }

        return new CatalogueAssetExportResult(destinationDirectory, work.Select(item => item.Result!).ToArray());
    }

    private void EnsureSafeDestinationDirectory(
        string parent,
        string destinationDirectory,
        CatalogueAssetExportOptions options)
    {
        var parentIdentity = FileSystemSafety.CaptureDirectoryIdentity(parent);
        _beforeDestinationDirectoryCreation?.Invoke();
        DirectoryIdentity? createdDirectoryIdentity = null;

        try
        {
            FileSystemSafety.CreateDirectoryIfMissing(destinationDirectory, out createdDirectoryIdentity);
            _afterDestinationDirectoryCreation?.Invoke();
            if (!FileSystemSafety.IsPathWithinDirectory(destinationDirectory, parent) ||
                !FileSystemSafety.HasSameDirectoryIdentity(parentIdentity) ||
                !FileSystemSafety.IsSafePathWithinDirectory(destinationDirectory, parent))
                throw new InvalidDataException($"Thư mục cha đích thay đổi thành symbolic link, junction, reparse point hoặc thư mục khác; từ chối xuất {options.AssetLabel}.");
        }
        catch
        {
            if (createdDirectoryIdentity is { } identity)
                FileSystemSafety.TryRemoveEmptyDirectoryWithIdentity(destinationDirectory, identity);
            throw;
        }
    }

    private static List<ExportWorkItem> ResolveRequests(
        IReadOnlyList<string> requestedCodes,
        string root,
        string destinationDirectory,
        string assetLabel,
        Func<string, string, CancellationToken, CatalogueAssetResolution> resolve,
        CancellationToken cancellationToken)
    {
        var work = new List<ExportWorkItem>(requestedCodes.Count);
        var seenCodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var code in requestedCodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = new ExportWorkItem(code);
            work.Add(item);
            var codeKey = TextNormalization.ProductKey(code);
            if (seenCodes.TryGetValue(codeKey, out var firstCode))
            {
                item.Result = Skipped(code, $"Mã trùng với '{firstCode}'; không sao chép lại.");
                continue;
            }
            seenCodes[codeKey] = code;

            CatalogueAssetResolution resolution;
            try
            {
                resolution = resolve(code, root, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsPathOrDataFailure(exception))
            {
                item.Result = Error(code, $"Không resolve được {assetLabel}: {exception.Message}");
                continue;
            }

            if (resolution.Status != CatalogueAssetResolutionStatus.Found || string.IsNullOrWhiteSpace(resolution.SourcePath))
            {
                item.Result = resolution.Status == CatalogueAssetResolutionStatus.Missing
                    ? Missing(code, resolution.Message)
                    : Error(code, resolution.Message);
                continue;
            }

            try
            {
                item.SourcePath = Path.GetFullPath(resolution.SourcePath);
                var fileName = Path.GetFileName(item.SourcePath);
                if (fileName.Length == 0)
                {
                    item.Result = Error(code, $"Không resolve được {assetLabel}: file nguồn không có tên hợp lệ.");
                    continue;
                }

                item.DestinationPath = Path.Combine(destinationDirectory, fileName);
                item.ResolutionKind = resolution.ResolutionKind ?? "exact";
            }
            catch (Exception exception) when (IsPathOrDataFailure(exception))
            {
                item.Result = Error(code, $"Không resolve được {assetLabel}: {exception.Message}");
            }
        }

        return work;
    }

    private static void DeduplicateSources(IEnumerable<ExportWorkItem> work)
    {
        var sources = new Dictionary<string, ExportWorkItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in work.Where(item => item.Result is null))
        {
            if (sources.TryGetValue(item.SourcePath!, out var first))
            {
                item.Result = Skipped(
                    item.Code,
                    $"Trùng file nguồn với mã '{first.Code}'; không sao chép lại {Path.GetFileName(item.SourcePath)}.",
                    item.SourcePath,
                    item.DestinationPath);
                continue;
            }
            sources[item.SourcePath!] = item;
        }
    }

    private static void RejectDestinationCollisions(
        IEnumerable<ExportWorkItem> work,
        CancellationToken cancellationToken)
    {
        var destinationCollisions = work
            .Where(item => item.Result is null)
            .GroupBy(item => item.DestinationPath!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(item => item.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any())
            .Select(group => group.ToArray())
            .ToArray();

        foreach (var collision in destinationCollisions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = collision[0];
            bool identical;
            try
            {
                identical = collision
                    .Skip(1)
                    .All(item => FilesHaveSameContent(
                        first.SourcePath!,
                        item.SourcePath!,
                        cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsFileFailure(exception))
            {
                foreach (var item in collision)
                    item.Result = Error(
                        item.Code,
                        $"Không kiểm tra được các file nguồn trùng tên '{Path.GetFileName(item.DestinationPath)}': {exception.Message}",
                        item.SourcePath,
                        item.DestinationPath);
                continue;
            }

            if (identical)
            {
                foreach (var item in collision.Skip(1))
                    item.Result = Skipped(
                        item.Code,
                        $"Trùng tên file đích '{Path.GetFileName(item.DestinationPath)}' nhưng cùng nội dung SHA-256 với mã '{first.Code}'; chỉ sao chép một lần.",
                        item.SourcePath,
                        item.DestinationPath);
                continue;
            }

            var sourceList = string.Join("; ", collision.Select(item => item.SourcePath).OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
            foreach (var item in collision)
                item.Result = Error(
                    item.Code,
                    $"Trùng tên file đích '{Path.GetFileName(item.DestinationPath)}' từ các nguồn khác nội dung; không sao chép: {sourceList}",
                    item.SourcePath,
                    item.DestinationPath);
        }
    }

    private static void Publish(ExportWorkItem item, CancellationToken cancellationToken)
    {
        var source = item.SourcePath!;
        var destination = item.DestinationPath!;
        var sourceDirectory = Path.GetDirectoryName(source)!;
        if (!FileSystemSafety.IsSafePathWithinDirectory(source, sourceDirectory))
        {
            item.Result = Error(item.Code, $"File nguồn chứa symbolic link, junction hoặc reparse point; không sao chép {Path.GetFileName(source)}.", source, destination);
            return;
        }
        var destinationDirectory = Path.GetDirectoryName(destination)!;
        if (!FileSystemSafety.IsSafePathWithinDirectory(destination, destinationDirectory))
        {
            item.Result = Error(item.Code, $"File đích chứa symbolic link, junction hoặc reparse point; không sao chép {Path.GetFileName(destination)}.", source, destination);
            return;
        }
        if (source.Equals(destination, StringComparison.OrdinalIgnoreCase))
        {
            item.Result = Skipped(item.Code, "File nguồn đã nằm tại đích; giữ nguyên.", source, destination);
            return;
        }
        if (File.Exists(destination))
        {
            try
            {
                item.Result = FilesHaveSameContent(source, destination, cancellationToken)
                    ? Skipped(item.Code, $"File đích cùng nội dung đã tồn tại; giữ nguyên {Path.GetFileName(destination)}.", source, destination)
                    : Error(item.Code, $"File đích cùng tên nhưng khác nội dung đã tồn tại; không ghi đè {Path.GetFileName(destination)}.", source, destination);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsFileFailure(exception))
            {
                item.Result = Error(item.Code, $"Không kiểm tra được file đích {Path.GetFileName(destination)}: {exception.Message}", source, destination);
            }
            return;
        }
        if (Directory.Exists(destination))
        {
            item.Result = Error(item.Code, $"Đích '{destination}' là thư mục; không thể sao chép file.", source, destination);
            return;
        }

        try
        {
            FileSystemSafety.CopyFileForContainedPublication(
                source,
                destination,
                destinationDirectory,
                cancellationToken);
            item.Result = new CatalogueAssetExportItem(
                item.Code,
                CatalogueAssetExportStatus.Copied,
                $"Đã sao chép {Path.GetFileName(source)} ({item.ResolutionKind}).",
                source,
                destination);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsFileFailure(exception) || exception is InvalidDataException)
        {
            item.Result = Error(item.Code, $"Không sao chép được {Path.GetFileName(source)}: {exception.Message}", source, destination);
        }
    }

    private static bool FilesHaveSameContent(string left, string right, CancellationToken cancellationToken)
    {
        if (new FileInfo(left).Length != new FileInfo(right).Length) return false;
        return CryptographicOperations.FixedTimeEquals(
            HashFile(left, cancellationToken),
            HashFile(right, cancellationToken));
    }

    private static byte[] HashFile(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return hash.GetHashAndReset();
    }

    private static string ExistingDirectory(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var full = FileSystemSafety.NormalizeDirectoryPath(value);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"Thư mục không tồn tại: {full}");
        return full;
    }

    private static bool IsFileFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or NotSupportedException or SecurityException;

    private static bool IsPathOrDataFailure(Exception exception) =>
        IsFileFailure(exception) || exception is InvalidDataException or ArgumentException;

    private static CatalogueAssetExportItem Skipped(string code, string message, string? source = null, string? destination = null) =>
        new(code, CatalogueAssetExportStatus.Skipped, message, source, destination);

    private static CatalogueAssetExportItem Missing(string code, string message) =>
        new(code, CatalogueAssetExportStatus.Missing, message);

    private static CatalogueAssetExportItem Error(string code, string message, string? source = null, string? destination = null) =>
        new(code, CatalogueAssetExportStatus.Error, message, source, destination);

    private sealed class ExportWorkItem(string code)
    {
        public string Code { get; } = code;
        public string? SourcePath { get; set; }
        public string? DestinationPath { get; set; }
        public string ResolutionKind { get; set; } = string.Empty;
        public CatalogueAssetExportItem? Result { get; set; }
    }
}
