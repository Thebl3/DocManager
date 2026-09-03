using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocManager.Core;

namespace DocManager.Signify;

/// <summary>
/// Persists the verified Signify identity that owns a canonical asset basename.
/// The manifest is deliberately family-local in its entries and root-local on disk,
/// so separate DocManager processes cannot silently assign the same asset to two SKUs.
/// </summary>
public sealed class CanonicalAssetManifest
{
    public const string FileName = ".docmanager-canonical-assets.json";
    private const int CurrentVersion = 1;
    private const int MaximumEntries = 100_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _root;
    private readonly string _path;
    private readonly string _mutexName;

    public CanonicalAssetManifest(string outputRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        _root = FileSystemSafety.NormalizeDirectoryPath(outputRoot);
        if (!FileSystemSafety.HasNoReparsePointsInExistingPath(_root))
            throw new ArgumentException("Product Family root chứa symbolic link, junction hoặc reparse point.", nameof(outputRoot));
        _path = Path.Combine(_root, FileName);
        _mutexName = $"DocManager.CanonicalAssets.{HashPath(_root)}";
    }

    public CanonicalAssetManifestLookup Resolve(ProductRecord record, string? matchedSku = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        return WithLock(entries =>
        {
            var family = WindowsFileName.Sanitize(record.ProductFamily);
            var material = IdentityKey(record.Material);
            var matched = IdentityKey(matchedSku);
            if (material.Length == 0 && matched.Length == 0) return new CanonicalAssetManifestLookup(null, false);

            var matches = entries.Where(entry =>
                    entry.ProductFamily.Equals(family, StringComparison.OrdinalIgnoreCase) &&
                    (SharesVerifiedIdentity(entry, material, matched) ||
                     (SameIdentity(entry.MatchedSku, material) &&
                      (matched.Length == 0 || SameIdentity(entry.MatchedSku, matched)))))
                .ToArray();
            if (matches.Length == 0) return new CanonicalAssetManifestLookup(null, false);

            var identities = matches
                .Select(entry => $"{entry.CanonicalBaseName}{entry.PdfPath}{entry.IesPath}{entry.LdtPath}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return identities.Length == 1
                ? new CanonicalAssetManifestLookup(matches[0], false)
                : new CanonicalAssetManifestLookup(null, true);
        });
    }

    public CanonicalAssetManifestReservation Reserve(
        ProductRecord record,
        ProductMatch match,
        string canonicalBaseName,
        string pdfPath,
        string? iesPath)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(match);
        var family = WindowsFileName.Sanitize(record.ProductFamily);
        var basename = ProductAssetName.CanonicalBaseName(canonicalBaseName);
        var expectedPdf = ExpectedPath(family, basename, ".pdf");
        var expectedIes = ExpectedPath(family, basename, ".ies", "IES");
        var expectedLdt = ExpectedPath(family, basename, ".ldt", "LDT");
        RequireExpectedPath(pdfPath, expectedPdf, nameof(pdfPath));
        if (!string.IsNullOrWhiteSpace(iesPath)) RequireExpectedPath(iesPath, expectedIes, nameof(iesPath));

        return WithLock(entries =>
        {
            var material = record.Material?.Trim() ?? string.Empty;
            var matchedSku = match.MatchedSku?.Trim() ?? string.Empty;
            var conflictingVerifiedMaterial = entries.Any(entry =>
                entry.ProductFamily.Equals(family, StringComparison.OrdinalIgnoreCase) &&
                SameIdentity(entry.Material, material) &&
                IdentityKey(entry.MatchedSku).Length > 0 &&
                IdentityKey(matchedSku).Length > 0 &&
                !SameIdentity(entry.MatchedSku, matchedSku));
            if (conflictingVerifiedMaterial)
                return new CanonicalAssetManifestReservation(null, true, "Material đã được xác minh với matched SKU khác trong cùng family.");

            var sameBasename = entries.Where(entry =>
                    entry.ProductFamily.Equals(family, StringComparison.OrdinalIgnoreCase) &&
                    entry.CanonicalBaseName.Equals(basename, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var compatible = sameBasename.Where(entry => SharesVerifiedIdentity(entry, material, matchedSku)).ToArray();
            if (sameBasename.Length > 0 && compatible.Length == 0)
                return new CanonicalAssetManifestReservation(null, true, "Canonical basename đã được matched SKU khác sử dụng trong cùng family.");

            if (sameBasename.Length == 0 &&
                (File.Exists(expectedPdf) || File.Exists(expectedIes) || File.Exists(expectedLdt)))
            {
                return new CanonicalAssetManifestReservation(null, true, "Canonical asset hiện có không có manifest xác minh identity; từ chối ghi đè.");
            }

            var existing = compatible.FirstOrDefault(entry => SameIdentity(entry.Material, material))
                ?? compatible.FirstOrDefault(entry => SameIdentity(entry.MatchedSku, matchedSku));
            CanonicalAssetManifestEntry entry;
            if (existing is null)
            {
                entry = new CanonicalAssetManifestEntry
                {
                    Material = material,
                    MatchedSku = matchedSku,
                    MatchedName = match.MatchedName?.Trim() ?? record.MaterialDescription?.Trim() ?? string.Empty,
                    ProductFamily = family,
                    CanonicalBaseName = basename,
                    PdfPath = Path.GetRelativePath(_root, expectedPdf),
                    IesPath = string.IsNullOrWhiteSpace(iesPath) ? string.Empty : Path.GetRelativePath(_root, expectedIes)
                };
                entries.Add(entry);
            }
            else
            {
                entry = existing with
                {
                    MatchedSku = string.IsNullOrWhiteSpace(matchedSku) ? existing.MatchedSku : matchedSku,
                    MatchedName = string.IsNullOrWhiteSpace(match.MatchedName) ? existing.MatchedName : match.MatchedName.Trim()
                };
                entries[entries.IndexOf(existing)] = entry;
            }

            Write(entries);
            return new CanonicalAssetManifestReservation(entry, false, string.Empty);
        });
    }

    public void UpdateDownloadedPaths(CanonicalAssetManifestEntry entry, string? pdfPath = null, string? iesPath = null, string? ldtPath = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (pdfPath is null && iesPath is null && ldtPath is null) return;
        WithLock(entries =>
        {
            var index = entries.FindIndex(candidate =>
                candidate.ProductFamily.Equals(entry.ProductFamily, StringComparison.OrdinalIgnoreCase) &&
                candidate.CanonicalBaseName.Equals(entry.CanonicalBaseName, StringComparison.OrdinalIgnoreCase) &&
                (SameIdentity(candidate.Material, entry.Material) || SameIdentity(candidate.MatchedSku, entry.MatchedSku)));
            if (index < 0) throw new InvalidDataException("Không tìm thấy identity manifest để cập nhật asset.");

            var current = entries[index];
            var updated = current with
            {
                PdfPath = pdfPath is null ? current.PdfPath : RelativeExpectedPath(pdfPath, current.ProductFamily, current.CanonicalBaseName, ".pdf"),
                IesPath = iesPath is null ? current.IesPath : RelativeExpectedPath(iesPath, current.ProductFamily, current.CanonicalBaseName, ".ies", "IES"),
                LdtPath = ldtPath is null ? current.LdtPath : RelativeExpectedPath(ldtPath, current.ProductFamily, current.CanonicalBaseName, ".ldt", "LDT")
            };
            entries[index] = updated;
            Write(entries);
            return 0;
        });
    }

    public IReadOnlyList<string> ExistingPaths(CanonicalAssetManifestEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new[] { entry.PdfPath, entry.IesPath, entry.LdtPath }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(AbsolutePath)
            .Where(File.Exists)
            .ToArray();
    }

    public string? ExistingPath(CanonicalAssetManifestEntry entry, string extension)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        var relative = extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? entry.PdfPath :
            extension.Equals(".ies", StringComparison.OrdinalIgnoreCase) ? entry.IesPath :
            extension.Equals(".ldt", StringComparison.OrdinalIgnoreCase) ? entry.LdtPath :
            throw new ArgumentException("Unsupported canonical asset extension.", nameof(extension));
        if (string.IsNullOrWhiteSpace(relative)) return null;
        var path = AbsolutePath(relative);
        return File.Exists(path) ? path : null;
    }

    private T WithLock<T>(Func<List<CanonicalAssetManifestEntry>, T> action)
    {
        using var mutex = new Mutex(false, _mutexName);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException($"Hết thời gian chờ khóa manifest: {_path}");
            return action(Read());
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
    }

    private List<CanonicalAssetManifestEntry> Read()
    {
        RequireSafeManifestPath();
        if (!File.Exists(_path)) return [];
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var document = JsonSerializer.Deserialize<CanonicalAssetManifestDocument>(stream) ?? throw new InvalidDataException("Manifest canonical asset rỗng.");
            if (document.Version != CurrentVersion || document.Entries is null || document.Entries.Count > MaximumEntries)
                throw new InvalidDataException("Manifest canonical asset có schema không hợp lệ.");
            foreach (var entry in document.Entries) Validate(entry);
            return [.. document.Entries];
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Manifest canonical asset không phải JSON hợp lệ.", exception);
        }
    }

    private void Write(List<CanonicalAssetManifestEntry> entries)
    {
        RequireSafeManifestPath();
        Directory.CreateDirectory(_root);
        RequireSafeManifestPath();
        var temporary = AtomicFile.CreateSiblingTemporaryPath(_path);
        try
        {
            var payload = JsonSerializer.Serialize(new CanonicalAssetManifestDocument { Version = CurrentVersion, Entries = entries }, JsonOptions);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
            {
                writer.Write(payload);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            RequireSafeManifestPath();
            AtomicFile.Replace(temporary, _path);
        }
        finally
        {
            if (FileSystemSafety.IsSafePathWithinDirectory(temporary, _root) && File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private void Validate(CanonicalAssetManifestEntry entry)
    {
        if (entry is null || entry.Material.Length > 512 || entry.MatchedSku.Length > 512 || entry.MatchedName.Length > 4096 ||
            entry.ProductFamily.Length == 0 || entry.ProductFamily.Length > 120 || entry.CanonicalBaseName.Length == 0 || entry.CanonicalBaseName.Length > ProductAssetName.MaximumBaseNameLength ||
            !entry.ProductFamily.Equals(WindowsFileName.Sanitize(entry.ProductFamily), StringComparison.Ordinal) ||
            !entry.CanonicalBaseName.Equals(ProductAssetName.CanonicalBaseName(entry.CanonicalBaseName), StringComparison.Ordinal))
            throw new InvalidDataException("Manifest canonical asset có identity không hợp lệ.");
        ValidateRelativePath(entry.PdfPath, entry.ProductFamily, entry.CanonicalBaseName, ".pdf");
        ValidateRelativePath(entry.IesPath, entry.ProductFamily, entry.CanonicalBaseName, ".ies", "IES");
        ValidateRelativePath(entry.LdtPath, entry.ProductFamily, entry.CanonicalBaseName, ".ldt", "LDT");
    }

    private void ValidateRelativePath(string path, string family, string basename, string extension, params string[] directory)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (path.Length > 1024 || Path.IsPathRooted(path) || !path.Equals(RelativeExpectedPath(AbsolutePath(path), family, basename, extension, directory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Manifest canonical asset có đường dẫn không hợp lệ.");
    }

    private string RelativeExpectedPath(string path, string family, string basename, string extension, params string[] directory)
    {
        var expected = ExpectedPath(family, basename, extension, directory);
        RequireExpectedPath(path, expected, nameof(path));
        return Path.GetRelativePath(_root, expected);
    }

    private string AbsolutePath(string relative)
    {
        if (Path.IsPathRooted(relative)) throw new InvalidDataException("Manifest canonical asset không được chứa đường dẫn tuyệt đối.");
        var full = Path.GetFullPath(Path.Combine(_root, relative));
        if (!FileSystemSafety.IsSafePathWithinDirectory(full, _root))
            throw new InvalidDataException("Manifest canonical asset có đường dẫn ngoài Product Family root hoặc qua symbolic link, junction, reparse point.");
        return full;
    }

    private string ExpectedPath(string family, string basename, string extension, params string[] directory) =>
        Path.Combine([_root, family, .. directory, $"{basename}{extension}"]);

    private void RequireExpectedPath(string path, string expected, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        var full = Path.GetFullPath(path);
        if (!FileSystemSafety.IsSafePathWithinDirectory(full, _root) || !full.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Canonical asset path không khớp family/canonical basename hoặc qua symbolic link, junction, reparse point.", parameterName);
    }

    private void RequireSafeManifestPath()
    {
        if (!FileSystemSafety.IsSafePathWithinDirectory(_path, _root))
            throw new InvalidDataException("Manifest canonical asset đi qua symbolic link, junction hoặc reparse point.");
    }

    private bool IsUnderRoot(string path) => FileSystemSafety.IsPathWithinDirectory(path, _root);
    private static bool SharesVerifiedIdentity(CanonicalAssetManifestEntry entry, string material, string matchedSku)
    {
        var entryMatched = IdentityKey(entry.MatchedSku);
        var incomingMatched = IdentityKey(matchedSku);
        if (SameIdentity(entry.Material, material))
        {
            return entryMatched.Length == 0 ||
                   incomingMatched.Length == 0 ||
                   entryMatched.Equals(incomingMatched, StringComparison.OrdinalIgnoreCase);
        }

        return incomingMatched.Length > 0 &&
               entryMatched.Length > 0 &&
               entryMatched.Equals(incomingMatched, StringComparison.OrdinalIgnoreCase);
    }
    private static bool SameIdentity(string? left, string? right)
    {
        var leftKey = IdentityKey(left);
        var rightKey = IdentityKey(right);
        return leftKey.Length > 0 && rightKey.Length > 0 && leftKey.Equals(rightKey, StringComparison.OrdinalIgnoreCase);
    }
    private static string IdentityKey(string? value) => TextNormalization.ProductKey(value);
    private static string HashPath(string path)
    {
        var normalized = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}

public sealed record CanonicalAssetManifestEntry
{
    public string Material { get; init; } = string.Empty;
    public string MatchedSku { get; init; } = string.Empty;
    public string MatchedName { get; init; } = string.Empty;
    public string ProductFamily { get; init; } = string.Empty;
    public string CanonicalBaseName { get; init; } = string.Empty;
    public string PdfPath { get; init; } = string.Empty;
    public string IesPath { get; init; } = string.Empty;
    public string LdtPath { get; init; } = string.Empty;
}

public sealed record CanonicalAssetManifestLookup(CanonicalAssetManifestEntry? Entry, bool HasCollision);
public sealed record CanonicalAssetManifestReservation(CanonicalAssetManifestEntry? Entry, bool HasCollision, string Error);

public sealed record CanonicalAssetManifestDocument
{
    public int Version { get; init; }
    public List<CanonicalAssetManifestEntry> Entries { get; init; } = [];
}
