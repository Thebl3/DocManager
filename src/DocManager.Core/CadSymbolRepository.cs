using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocManager.Core;

public sealed class CadSymbolRepository
{
    public const int SchemaVersion = 1;
    public const int MaximumImageBytes = 16 * 1024 * 1024;
    public const int MaximumImageDimension = 8192;
    public const long MaximumImagePixels = 40_000_000;
    private const int MaximumEntries = 100_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _root;
    private readonly string _imagesRoot;
    private readonly string _manifestPath;
    private readonly string _mutexName;
    private static readonly Dictionary<string, object> ProcessLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly object ProcessLocksGate = new();
    private readonly Action? _beforeManifestCommit;
    private readonly object _diagnosticGate = new();
    private IReadOnlyList<string> _diagnostics = [];

    public CadSymbolRepository(string? root = null) : this(root, null)
    {
    }

    internal CadSymbolRepository(string? root, Action? beforeManifestCommit)
    {
        var selectedRoot = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DocManager",
            "cad-symbols");
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedRoot);
        _root = FileSystemSafety.NormalizeDirectoryPath(selectedRoot);
        if (!FileSystemSafety.HasNoReparsePointsInExistingPath(_root))
            throw new ArgumentException("CAD symbol root chứa symbolic link, junction hoặc reparse point.", nameof(root));
        _imagesRoot = Path.Combine(_root, "images");
        _manifestPath = Path.Combine(_root, "manifest.json");
        _mutexName = $"Global\\DocManager.CadSymbols.{HashText(NormalizePathIdentity(_root))}";
        _beforeManifestCommit = beforeManifestCommit;
    }

    public string RootPath => _root;
    public string ManifestPath => _manifestPath;
    public IReadOnlyList<string> Diagnostics
    {
        get { lock (_diagnosticGate) return _diagnostics.ToArray(); }
    }

    public static string ModelKey(string? displayModel)
    {
        var productKey = TextNormalization.ProductKey(displayModel);
        return productKey.Length == 0 ? string.Empty : $"v1|{productKey}";
    }

    public IReadOnlyList<CadSymbolMapping> GetMappings() => WithLock(state =>
        state.Entries
            .Where(entry => entry.HasImage)
            .Select(ToMapping)
            .OrderBy(entry => entry.DisplayModel, StringComparer.CurrentCultureIgnoreCase)
            .ToArray());

    public IReadOnlyList<CadSymbolModel> GetModels() => WithLock(state =>
        state.Entries
            .Select(ToModel)
            .OrderBy(entry => entry.DisplayModel, StringComparer.CurrentCultureIgnoreCase)
            .ToArray());

    public CadSymbolMapping? GetMapping(string displayModelOrKey)
    {
        var key = NormalizeRequestedKey(displayModelOrKey);
        if (key.Length == 0) return null;
        return WithLock(state =>
        {
            var entry = state.Entries.FirstOrDefault(candidate => candidate.Key.Equals(key, StringComparison.Ordinal));
            return entry?.HasImage == true ? ToMapping(entry) : null;
        });
    }

    public IReadOnlyList<CadSymbolModel> RegisterModels(IEnumerable<CadSymbolModelRegistration> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        var registrations = models.Select(ValidateRegistration).DistinctBy(registration => registration.Key, StringComparer.Ordinal).ToArray();
        return WithLock(state =>
        {
            var changed = false;
            foreach (var registration in registrations)
            {
                var index = state.Entries.FindIndex(entry => entry.Key.Equals(registration.Key, StringComparison.Ordinal));
                if (index < 0)
                {
                    state.Entries.Add(new CadSymbolManifestEntry
                    {
                        Key = registration.Key,
                        DisplayModel = registration.DisplayModel,
                        UpdatedUtc = DateTimeOffset.UtcNow,
                        Source = NormalizeSource(registration.Source)
                    });
                    changed = true;
                }
                else if (!state.Entries[index].DisplayModel.Equals(registration.DisplayModel, StringComparison.Ordinal))
                {
                    state.Entries[index] = state.Entries[index] with { DisplayModel = registration.DisplayModel };
                    changed = true;
                }
            }
            if (changed) WriteManifest(state);
            return registrations.Select(registration =>
                ToModel(state.Entries.First(entry => entry.Key.Equals(registration.Key, StringComparison.Ordinal)))).ToArray();
        }, writeIntent: true);
    }

    public CadSymbolMapping Upsert(string displayModel, byte[] canonicalPng, string source)
    {
        ArgumentNullException.ThrowIfNull(canonicalPng);
        var validatedPng = canonicalPng.ToArray();
        var registration = ValidateRegistration(new CadSymbolModelRegistration(displayModel, source));
        var dimensions = PngValidation.Validate(validatedPng);
        var hash = Convert.ToHexString(SHA256.HashData(validatedPng)).ToLowerInvariant();
        var relativePath = $"images/{hash}.png";
        return WithLock(state =>
        {
            var imageWasCreated = false;
            try
            {
                imageWasCreated = WriteImage(hash, validatedPng);
                var now = DateTimeOffset.UtcNow;
                var entry = new CadSymbolManifestEntry
                {
                    Key = registration.Key,
                    DisplayModel = registration.DisplayModel,
                    ImagePath = relativePath,
                    ImageHash = hash,
                    PixelWidth = dimensions.Width,
                    PixelHeight = dimensions.Height,
                    UpdatedUtc = now,
                    Source = NormalizeSource(source)
                };
                var index = state.Entries.FindIndex(candidate => candidate.Key.Equals(entry.Key, StringComparison.Ordinal));
                if (index < 0) state.Entries.Add(entry); else state.Entries[index] = entry;
                _beforeManifestCommit?.Invoke();
                WriteManifest(state);
                return ToMapping(entry);
            }
            catch
            {
                if (imageWasCreated) TryDeleteNewUnreferencedImage(hash);
                throw;
            }
        }, writeIntent: true);
    }

    public bool RemoveMapping(string displayModelOrKey, string source = "user-remove")
    {
        var key = NormalizeRequestedKey(displayModelOrKey);
        if (key.Length == 0) return false;
        return WithLock(state =>
        {
            var index = state.Entries.FindIndex(entry => entry.Key.Equals(key, StringComparison.Ordinal));
            if (index < 0 || !state.Entries[index].HasImage) return false;
            state.Entries[index] = state.Entries[index] with
            {
                ImagePath = string.Empty,
                ImageHash = string.Empty,
                PixelWidth = 0,
                PixelHeight = 0,
                UpdatedUtc = DateTimeOffset.UtcNow,
                Source = NormalizeSource(source)
            };
            WriteManifest(state);
            return true;
        }, writeIntent: true);
    }

    public bool Remove(string displayModelOrKey)
    {
        var key = NormalizeRequestedKey(displayModelOrKey);
        if (key.Length == 0) return false;
        return WithLock(state =>
        {
            var removed = state.Entries.RemoveAll(entry => entry.Key.Equals(key, StringComparison.Ordinal)) > 0;
            if (removed) WriteManifest(state);
            return removed;
        }, writeIntent: true);
    }

    public byte[] ReadImage(CadSymbolMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        return WithLock(_ =>
        {
            var path = ResolveImagePath(mapping.ImagePath, mapping.ImageHash);
            var bytes = ReadValidatedImage(path);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!hash.Equals(mapping.ImageHash, StringComparison.Ordinal))
                throw new InvalidDataException("Ảnh CAD symbol không khớp SHA-256 trong manifest.");
            return bytes;
        });
    }

    private T WithLock<T>(Func<CadSymbolManifestDocument, T> action, bool writeIntent = false)
    {
        var processLock = ProcessLockFor(_root);
        lock (processLock)
        {
            using var mutex = CreateRepositoryMutex();
            var acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new IOException($"Hết thời gian chờ khóa CAD symbol: {_manifestPath}");
                var read = ReadManifest();
                if (writeIntent && read.IsCorrupt) ArchiveCorruptManifest();
                return action(read.Document);
            }
            finally
            {
                if (acquired) mutex.ReleaseMutex();
            }
        }
    }

    private Mutex CreateRepositoryMutex()
    {
        try
        {
            return new Mutex(false, _mutexName);
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            // Global namespace access can be restricted by local policy. Retain
            // deterministic process/session coordination rather than disabling locking.
            return new Mutex(false, _mutexName.Replace("Global\\", "Local\\", StringComparison.Ordinal));
        }
    }

    private static object ProcessLockFor(string root)
    {
        var key = NormalizePathIdentity(root);
        lock (ProcessLocksGate)
        {
            if (!ProcessLocks.TryGetValue(key, out var processLock))
            {
                processLock = new object();
                ProcessLocks.Add(key, processLock);
            }
            return processLock;
        }
    }

    private ManifestReadResult ReadManifest()
    {
        RequireSafeRepositoryPath(_manifestPath);
        if (!File.Exists(_manifestPath))
        {
            SetDiagnostics([]);
            return new ManifestReadResult(new CadSymbolManifestDocument(), false);
        }
        try
        {
            using var stream = new FileStream(_manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var document = JsonSerializer.Deserialize<CadSymbolManifestDocument>(stream, JsonOptions)
                ?? throw new InvalidDataException("Manifest CAD symbol rỗng.");
            ValidateDocument(document);
            SetDiagnostics([]);
            return new ManifestReadResult(document, false);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            SetDiagnostics([$"Không đọc được manifest CAD symbol; đang dùng danh sách rỗng: {exception.Message}"]);
            return new ManifestReadResult(new CadSymbolManifestDocument(), true);
        }
    }

    private bool WriteImage(string hash, byte[] png)
    {
        RequireSafeRepositoryPath(_imagesRoot);
        Directory.CreateDirectory(_imagesRoot);
        RequireSafeRepositoryPath(_imagesRoot);
        var imagesIdentity = FileSystemSafety.CaptureDirectoryIdentity(_imagesRoot);
        var destination = ResolveImagePath($"images/{hash}.png", hash);
        if (File.Exists(destination))
        {
            RequireUnchangedImagesDirectory(imagesIdentity, destination);
            var existing = ReadValidatedImage(destination);
            RequireUnchangedImagesDirectory(imagesIdentity, destination);
            if (CryptographicOperations.FixedTimeEquals(SHA256.HashData(existing), SHA256.HashData(png))) return false;
            throw new InvalidDataException("Ảnh CAD symbol trùng SHA-256 nhưng nội dung khác.");
        }
        var temporary = AtomicFile.CreateSiblingTemporaryPath(destination);
        var published = false;
        try
        {
            RequireUnchangedImagesDirectory(imagesIdentity, temporary);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            {
                stream.Write(png);
                stream.Flush(flushToDisk: true);
            }
            RequireUnchangedImagesDirectory(imagesIdentity, temporary);
            RequireUnchangedImagesDirectory(imagesIdentity, destination);
            File.Move(temporary, destination, overwrite: false);
            published = true;
            RequireUnchangedImagesDirectory(imagesIdentity, destination);
            return true;
        }
        catch
        {
            if (published) TryDeleteImagePublishedIntoUnchangedDirectory(destination, imagesIdentity);
            throw;
        }
        finally
        {
            try
            {
                if (FileSystemSafety.HasSameDirectoryIdentity(imagesIdentity) &&
                    FileSystemSafety.IsSafePathWithinDirectory(temporary, _imagesRoot) &&
                    File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
    }

    private void RequireUnchangedImagesDirectory(DirectoryIdentity expected, string path)
    {
        if (!FileSystemSafety.HasSameDirectoryIdentity(expected) ||
            !FileSystemSafety.IsSafePathWithinDirectory(path, _imagesRoot))
        {
            throw new InvalidDataException("Thư mục ảnh CAD symbol đã bị thay thế hoặc chuyển qua reparse point.");
        }
    }

    private static void TryDeleteImagePublishedIntoUnchangedDirectory(string path, DirectoryIdentity expected)
    {
        try
        {
            if (FileSystemSafety.HasSameDirectoryIdentity(expected) &&
                FileSystemSafety.IsSafePathWithinDirectory(path, expected.Path) &&
                File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) { }
    }

    private static byte[] ReadValidatedImage(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > MaximumImageBytes)
            throw new InvalidDataException("Ảnh CAD symbol vượt giới hạn dung lượng.");
        using var output = new MemoryStream((int)Math.Min(stream.Length, MaximumImageBytes));
        var buffer = new byte[81920];
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (output.Length + read > MaximumImageBytes)
                throw new InvalidDataException("Ảnh CAD symbol vượt giới hạn dung lượng.");
            output.Write(buffer, 0, read);
        }
        var bytes = output.ToArray();
        if (bytes.Length == 0) throw new InvalidDataException("Ảnh CAD symbol rỗng.");
        PngValidation.Validate(bytes);
        return bytes;
    }

    private void TryDeleteNewUnreferencedImage(string hash)
    {
        try
        {
            var manifest = ReadManifest();
            if (manifest.Document.Entries.Any(entry => entry.HasImage && entry.ImageHash.Equals(hash, StringComparison.OrdinalIgnoreCase))) return;
            var imagesIdentity = FileSystemSafety.CaptureDirectoryIdentity(_imagesRoot);
            var path = ResolveImagePath($"images/{hash}.png", hash);
            if (!FileSystemSafety.HasSameDirectoryIdentity(imagesIdentity) ||
                !FileSystemSafety.IsSafePathWithinDirectory(path, _imagesRoot) ||
                !File.Exists(path)) return;
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) { }
    }

    private void WriteManifest(CadSymbolManifestDocument document)
    {
        document = document with
        {
            SchemaVersion = SchemaVersion,
            Entries = document.Entries.OrderBy(entry => entry.Key, StringComparer.Ordinal).ToList()
        };
        ValidateDocument(document);
        RequireSafeRepositoryPath(_manifestPath);
        Directory.CreateDirectory(_root);
        RequireSafeRepositoryPath(_manifestPath);
        var rootIdentity = FileSystemSafety.CaptureDirectoryIdentity(_root);
        var temporary = AtomicFile.CreateSiblingTemporaryPath(_manifestPath);
        try
        {
            RequireUnchangedRepositoryRoot(rootIdentity, temporary);
            var payload = JsonSerializer.Serialize(document, JsonOptions);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
            {
                writer.Write(payload);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            RequireUnchangedRepositoryRoot(rootIdentity, temporary);
            RequireUnchangedRepositoryRoot(rootIdentity, _manifestPath);
            AtomicFile.Replace(temporary, _manifestPath);
            RequireUnchangedRepositoryRoot(rootIdentity, _manifestPath);
            SetDiagnostics([]);
        }
        finally
        {
            try
            {
                if (FileSystemSafety.HasSameDirectoryIdentity(rootIdentity) &&
                    FileSystemSafety.IsSafePathWithinDirectory(temporary, _root) &&
                    File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
    }

    private void RequireUnchangedRepositoryRoot(DirectoryIdentity expected, string path)
    {
        if (!FileSystemSafety.HasSameDirectoryIdentity(expected) ||
            !FileSystemSafety.IsSafePathWithinDirectory(path, _root))
        {
            throw new InvalidDataException("CAD symbol repository root đã bị thay thế hoặc chuyển qua reparse point.");
        }
    }

    private void ArchiveCorruptManifest()
    {
        RequireSafeRepositoryPath(_manifestPath);
        if (!File.Exists(_manifestPath)) return;
        var backup = Path.Combine(_root, $"manifest.{DateTime.UtcNow:yyyyMMddHHmmssfff}.corrupt.bak.json");
        for (var suffix = 0; File.Exists(backup); suffix++)
            backup = Path.Combine(_root, $"manifest.{DateTime.UtcNow:yyyyMMddHHmmssfff}.{suffix}.corrupt.bak.json");
        File.Move(_manifestPath, backup);
        SetDiagnostics([$"Manifest CAD symbol lỗi đã được giữ tại {backup}."]);
    }

    private void ValidateDocument(CadSymbolManifestDocument document)
    {
        if (document.SchemaVersion != SchemaVersion || document.Entries is null || document.Entries.Count > MaximumEntries)
            throw new InvalidDataException("Manifest CAD symbol có schema không hợp lệ.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in document.Entries)
        {
            if (entry is null || entry.Key.Length > 4096 || entry.DisplayModel.Length > 4096 || entry.Source.Length > 512 ||
                entry.Key.Length == 0 || entry.DisplayModel.Length == 0 || entry.UpdatedUtc == default ||
                !entry.Key.Equals(ModelKey(entry.DisplayModel), StringComparison.Ordinal) || !keys.Add(entry.Key))
                throw new InvalidDataException("Manifest CAD symbol có model identity không hợp lệ hoặc trùng.");
            if (!entry.HasImage)
            {
                if (entry.ImagePath.Length != 0 || entry.ImageHash.Length != 0 || entry.PixelWidth != 0 || entry.PixelHeight != 0)
                    throw new InvalidDataException("Manifest CAD symbol có mapping ảnh không đầy đủ.");
                continue;
            }
            ResolveImagePath(entry.ImagePath, entry.ImageHash);
            if (entry.PixelWidth <= 0 || entry.PixelHeight <= 0 || entry.PixelWidth > MaximumImageDimension || entry.PixelHeight > MaximumImageDimension ||
                (long)entry.PixelWidth * entry.PixelHeight > MaximumImagePixels)
                throw new InvalidDataException("Manifest CAD symbol có kích thước ảnh không hợp lệ.");
        }
    }

    private string ResolveImagePath(string relativePath, string expectedHash)
    {
        if (relativePath.Length > 256 || Path.IsPathRooted(relativePath) || expectedHash.Length != 64 || expectedHash.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("Manifest CAD symbol có đường dẫn/hash ảnh không hợp lệ.");
        var normalizedRelative = relativePath.Replace('\\', '/');
        var expected = $"images/{expectedHash.ToLowerInvariant()}.png";
        if (!normalizedRelative.Equals(expected, StringComparison.Ordinal))
            throw new InvalidDataException("Manifest CAD symbol có đường dẫn ảnh không chuẩn.");
        var full = Path.GetFullPath(Path.Combine(_root, relativePath));
        RequireSafeRepositoryPath(full);
        return full;
    }

    private void RequireSafeRepositoryPath(string path)
    {
        if (!FileSystemSafety.IsSafePathWithinDirectory(path, _root))
            throw new InvalidDataException("CAD symbol repository đi qua symbolic link, junction hoặc reparse point.");
    }

    private static CadSymbolModelRegistration ValidateRegistration(CadSymbolModelRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var display = registration.DisplayModel?.Trim() ?? string.Empty;
        var key = ModelKey(display);
        if (key.Length == 0 || display.Length > 4096) throw new ArgumentException("Proposed calculation model không hợp lệ.", nameof(registration));
        return registration with { DisplayModel = display, Key = key, Source = NormalizeSource(registration.Source) };
    }

    private static string NormalizeRequestedKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim();
        return trimmed.StartsWith("v1|", StringComparison.Ordinal) ? trimmed : ModelKey(trimmed);
    }

    private static string NormalizeSource(string? source)
    {
        var normalized = source?.Trim() ?? string.Empty;
        return normalized.Length <= 512 ? normalized : normalized[..512];
    }

    private static CadSymbolMapping ToMapping(CadSymbolManifestEntry entry) => new(
        entry.Key, entry.DisplayModel, entry.ImagePath, entry.ImageHash, entry.PixelWidth, entry.PixelHeight, entry.UpdatedUtc, entry.Source);
    private static CadSymbolModel ToModel(CadSymbolManifestEntry entry) => new(
        entry.Key, entry.DisplayModel, entry.HasImage, entry.PixelWidth, entry.PixelHeight, entry.UpdatedUtc, entry.Source);
    private static string NormalizePathIdentity(string path) => OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
    private static string HashText(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private void SetDiagnostics(IReadOnlyList<string> diagnostics)
    {
        lock (_diagnosticGate)
        {
            if (diagnostics.Count == 0 && _diagnostics.Any(item => item.Contains("được giữ tại", StringComparison.Ordinal))) return;
            _diagnostics = diagnostics.ToArray();
        }
    }

    private sealed record ManifestReadResult(CadSymbolManifestDocument Document, bool IsCorrupt);
}

public sealed record CadSymbolMapping(
    string Key,
    string DisplayModel,
    string ImagePath,
    string ImageHash,
    int PixelWidth,
    int PixelHeight,
    DateTimeOffset UpdatedUtc,
    string Source);

public sealed record CadSymbolModel(
    string Key,
    string DisplayModel,
    bool HasImage,
    int PixelWidth,
    int PixelHeight,
    DateTimeOffset UpdatedUtc,
    string Source);

public sealed record CadSymbolModelRegistration(string DisplayModel, string Source = "workbook")
{
    public string Key { get; init; } = string.Empty;
}

public sealed record CadSymbolManifestDocument
{
    public int SchemaVersion { get; init; } = CadSymbolRepository.SchemaVersion;
    public List<CadSymbolManifestEntry> Entries { get; init; } = [];
}

public sealed record CadSymbolManifestEntry
{
    public string Key { get; init; } = string.Empty;
    public string DisplayModel { get; init; } = string.Empty;
    public string ImagePath { get; init; } = string.Empty;
    public string ImageHash { get; init; } = string.Empty;
    public int PixelWidth { get; init; }
    public int PixelHeight { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
    public string Source { get; init; } = string.Empty;
    public bool HasImage => ImagePath.Length > 0 && ImageHash.Length > 0;
}

public readonly record struct PngDimensions(int Width, int Height);

public static class PngValidation
{
    private const long MaximumDecodedImageBytes =
        (CadSymbolRepository.MaximumImagePixels * 8L) + CadSymbolRepository.MaximumImageDimension;
    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = CreateCrcTable();

    public static PngDimensions Validate(ReadOnlySpan<byte> png)
    {
        if (png.Length < 8 || png.Length > CadSymbolRepository.MaximumImageBytes || !png[..8].SequenceEqual(Signature))
            throw new InvalidDataException("Ảnh CAD symbol phải là PNG hợp lệ.");

        var offset = Signature.Length;
        var sawIhdr = false;
        var sawPlte = false;
        var sawIdat = false;
        var idatEnded = false;
        var width = 0;
        var height = 0;
        var bitDepth = 0;
        var colorType = 0;
        using var compressedImageData = new MemoryStream();

        while (offset < png.Length)
        {
            if (png.Length - offset < 12) throw new InvalidDataException("PNG CAD symbol bị cắt cụt.");
            var length = ReadUInt32(png.Slice(offset, 4));
            if (length > (uint)(png.Length - offset - 12)) throw new InvalidDataException("PNG CAD symbol có độ dài chunk không hợp lệ.");

            var type = png.Slice(offset + 4, 4);
            var data = png.Slice(offset + 8, (int)length);
            var storedCrc = ReadUInt32(png.Slice(offset + 8 + (int)length, 4));
            if (!IsValidChunkType(type) || CalculateCrc(type, data) != storedCrc)
                throw new InvalidDataException("PNG CAD symbol có chunk hoặc CRC không hợp lệ.");

            var isIhdr = type.SequenceEqual("IHDR"u8);
            var isPlte = type.SequenceEqual("PLTE"u8);
            var isIdat = type.SequenceEqual("IDAT"u8);
            var isIend = type.SequenceEqual("IEND"u8);
            if (!isIhdr && !isPlte && !isIdat && !isIend && IsCriticalChunk(type))
                throw new InvalidDataException("PNG CAD symbol có critical chunk không được hỗ trợ.");

            if (isIhdr)
            {
                if (sawIhdr || offset != Signature.Length || length != 13)
                    throw new InvalidDataException("PNG CAD symbol phải có đúng một IHDR đầu tiên.");
                (width, height, bitDepth, colorType) = ValidateIhdr(data);
                sawIhdr = true;
            }
            else
            {
                if (!sawIhdr) throw new InvalidDataException("PNG CAD symbol thiếu IHDR đầu tiên.");
                if (isPlte)
                {
                    if (sawPlte || sawIdat || colorType is 0 or 4 || length == 0 || length % 3 != 0 || length > 768 ||
                        (colorType == 3 && length / 3 > (1u << bitDepth)))
                        throw new InvalidDataException("PNG CAD symbol có PLTE không hợp lệ.");
                    sawPlte = true;
                }
                else if (isIdat)
                {
                    if (idatEnded || (colorType == 3 && !sawPlte) ||
                        compressedImageData.Length + length > CadSymbolRepository.MaximumImageBytes)
                    {
                        throw new InvalidDataException("PNG CAD symbol có thứ tự hoặc dung lượng IDAT không hợp lệ.");
                    }
                    compressedImageData.Write(data);
                    sawIdat = true;
                }
                else if (isIend)
                {
                    if (length != 0 || !sawIdat || offset + 12 != png.Length)
                        throw new InvalidDataException("PNG CAD symbol thiếu IEND kết thúc hợp lệ.");
                    ValidateImagePayload(compressedImageData, width, height, bitDepth, colorType);
                    return new PngDimensions(width, height);
                }
                else if (sawIdat)
                {
                    idatEnded = true;
                }
            }

            offset += 12 + (int)length;
        }

        throw new InvalidDataException("PNG CAD symbol thiếu IEND kết thúc.");
    }

    private static (int Width, int Height, int BitDepth, int ColorType) ValidateIhdr(ReadOnlySpan<byte> data)
    {
        var parsedWidth = ReadUInt32(data[..4]);
        var parsedHeight = ReadUInt32(data.Slice(4, 4));
        if (parsedWidth == 0 || parsedHeight == 0 || parsedWidth > CadSymbolRepository.MaximumImageDimension || parsedHeight > CadSymbolRepository.MaximumImageDimension ||
            (ulong)parsedWidth * parsedHeight > CadSymbolRepository.MaximumImagePixels)
            throw new InvalidDataException("Ảnh CAD symbol vượt giới hạn kích thước/pixel.");

        var bitDepth = data[8];
        var colorType = data[9];
        if (!HasValidBitDepth(colorType, bitDepth) || data[10] != 0 || data[11] != 0 || data[12] != 0)
            throw new InvalidDataException("PNG CAD symbol có IHDR không được hỗ trợ; ảnh interlaced không được chấp nhận.");

        return ((int)parsedWidth, (int)parsedHeight, bitDepth, colorType);
    }

    private static bool HasValidBitDepth(int colorType, int bitDepth) => colorType switch
    {
        0 => bitDepth is 1 or 2 or 4 or 8 or 16,
        2 or 4 or 6 => bitDepth is 8 or 16,
        3 => bitDepth is 1 or 2 or 4 or 8,
        _ => false
    };

    private static void ValidateImagePayload(
        MemoryStream compressedImageData,
        int width,
        int height,
        int bitDepth,
        int colorType)
    {
        var channels = colorType switch
        {
            0 or 3 => 1,
            2 => 3,
            4 => 2,
            6 => 4,
            _ => throw new InvalidDataException("PNG CAD symbol có color type không hợp lệ.")
        };
        long rowBytes;
        long expectedBytes;
        try
        {
            var bitsPerRow = checked((long)width * channels * bitDepth);
            rowBytes = checked((bitsPerRow + 7) / 8);
            expectedBytes = checked((rowBytes + 1) * height);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("PNG CAD symbol có kích thước scanline không hợp lệ.", exception);
        }
        if (rowBytes <= 0 || expectedBytes <= 0 || expectedBytes > MaximumDecodedImageBytes)
            throw new InvalidDataException("Dữ liệu giải nén PNG CAD symbol vượt giới hạn an toàn.");

        compressedImageData.Position = 0;
        try
        {
            using var inflater = new ZLibStream(compressedImageData, CompressionMode.Decompress, leaveOpen: true);
            var buffer = new byte[8192];
            long total = 0;
            long rowOffset = 0;
            while (total < expectedBytes)
            {
                var requested = (int)Math.Min(buffer.Length, expectedBytes - total);
                var read = inflater.Read(buffer, 0, requested);
                if (read == 0) break;
                for (var index = 0; index < read; index++)
                {
                    if (rowOffset == 0 && buffer[index] > 4)
                        throw new InvalidDataException("PNG CAD symbol có filter byte scanline không hợp lệ.");
                    rowOffset++;
                    if (rowOffset == rowBytes + 1) rowOffset = 0;
                }
                total += read;
            }
            if (total != expectedBytes || rowOffset != 0)
                throw new InvalidDataException("PNG CAD symbol có dữ liệu scanline bị thiếu hoặc cắt cụt.");
            if (inflater.ReadByte() != -1)
                throw new InvalidDataException("PNG CAD symbol có dữ liệu scanline dư hoặc vượt giới hạn.");
        }
        catch (InvalidDataException exception)
        {
            if (exception.Message.StartsWith("PNG CAD symbol", StringComparison.Ordinal)) throw;
            throw new InvalidDataException("PNG CAD symbol có payload zlib/deflate không hợp lệ.", exception);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException)
        {
            throw new InvalidDataException("PNG CAD symbol có payload zlib/deflate không hợp lệ.", exception);
        }
    }

    private static bool IsValidChunkType(ReadOnlySpan<byte> type) =>
        type.Length == 4 && IsAsciiLetter(type[0]) && IsAsciiLetter(type[1]) && IsAsciiLetter(type[2]) && IsAsciiLetter(type[3]) &&
        (type[2] & 0x20) == 0;

    private static bool IsAsciiLetter(byte value) =>
        value is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z';

    private static bool IsCriticalChunk(ReadOnlySpan<byte> type) => (type[0] & 0x20) == 0;

    private static uint CalculateCrc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in type) crc = CrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
        foreach (var value in data) crc = CrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
        return ~crc;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++) value = (value & 1) == 0 ? value >> 1 : 0xedb88320u ^ (value >> 1);
            table[index] = value;
        }
        return table;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes) =>
        ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
}
