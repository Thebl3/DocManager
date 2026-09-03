using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DocManager.Core;

public static class FileSystemSafety
{
    /// <summary>
    /// Normalizes a directory path without changing a filesystem root such as <c>C:\</c>
    /// or <c>\\server\share\</c>. <see cref="Path.TrimEndingDirectorySeparator"/> deliberately
    /// preserves roots; callers must not use string <c>TrimEnd</c> for this purpose.
    /// </summary>
    public static string NormalizeDirectoryPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var normalized = Path.TrimEndingDirectorySeparator(fullPath);
        if (normalized.Length == 0) throw new ArgumentException("Directory path is invalid.", nameof(path));
        return normalized;
    }

    public static bool IsPathWithinDirectory(string path, string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var fullPath = Path.GetFullPath(path);
        var fullDirectory = NormalizeDirectoryPath(directory);
        var relative = Path.GetRelativePath(fullDirectory, fullPath);
        return !Path.IsPathRooted(relative) &&
               !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns false if any existing lexical component of <paramref name="path"/>, including
    /// the path itself, is a symbolic link, junction, or another reparse point. Link targets
    /// are never resolved; errors while inspecting a component are treated as unsafe.
    /// </summary>
    public static bool HasNoReparsePointsInExistingPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root)) return false;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (var current = fullPath; ;)
        {
            try
            {
                if (IsReparsePoint(File.GetAttributes(current))) return false;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return false;
            }

            if (current.Equals(root, comparison)) return true;
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) || parent.Equals(current, comparison)) return false;
            current = parent;
        }
    }

    public static bool IsSafePathWithinDirectory(string path, string directory) =>
        IsPathWithinDirectory(path, directory) &&
        HasNoReparsePointsInExistingPath(directory) &&
        HasNoReparsePointsInExistingPath(path);

    /// <summary>
    /// Creates <paramref name="directory"/> when it does not already exist and returns whether
    /// this call created it. A preexisting directory is never reported as newly created.
    /// </summary>
    public static bool CreateDirectoryIfMissing(string directory) =>
        CreateDirectoryIfMissing(directory, out _);

    /// <summary>
    /// Creates a directory atomically and, when successful, captures its identity for safe
    /// best-effort cleanup if a later validation step fails.
    /// </summary>
    public static bool CreateDirectoryIfMissing(string directory, out DirectoryIdentity? createdDirectoryIdentity)
    {
        var fullDirectory = NormalizeDirectoryPath(directory);
        createdDirectoryIdentity = null;
        if (!OperatingSystem.IsWindows())
        {
            if (Directory.Exists(fullDirectory)) return false;
            Directory.CreateDirectory(fullDirectory);
            createdDirectoryIdentity = CaptureDirectoryIdentityWithoutAncestorSafety(fullDirectory);
            return true;
        }

        if (CreateDirectory(fullDirectory, IntPtr.Zero))
        {
            createdDirectoryIdentity = CaptureDirectoryIdentityWithoutAncestorSafety(fullDirectory);
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        if (error == ErrorAlreadyExists && Directory.Exists(fullDirectory)) return false;
        throw new IOException($"Cannot create directory: {new Win32Exception(error).Message}");
    }

    /// <summary>
    /// Removes an empty directory only when its identity matches a directory created by this
    /// process. Parent reparse points are allowed here solely to clean up a known-new child.
    /// </summary>
    public static void TryRemoveEmptyDirectoryWithIdentity(string directory, DirectoryIdentity expected)
    {
        try
        {
            var fullDirectory = NormalizeDirectoryPath(directory);
            if (!CaptureDirectoryIdentityWithoutAncestorSafety(fullDirectory).Equals(expected) ||
                Directory.EnumerateFileSystemEntries(fullDirectory).Any() ||
                !CaptureDirectoryIdentityWithoutAncestorSafety(fullDirectory).Equals(expected))
                return;
            Directory.Delete(fullDirectory, recursive: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // The directory may have been replaced or populated concurrently; preserve it.
        }
    }

    /// <summary>
    /// Copies one file into an existing, lexically-contained destination directory without
    /// following reparse points. The directory identity is checked immediately before and after
    /// the no-overwrite move. If a replacement is detected after publication, the method removes
    /// only the artifact it created when the destination is still a non-reparse path under the
    /// expected directory, then fails closed. Windows directory handles are not held open across
    /// the rename, so a privileged concurrent filesystem attacker can still race replacement and
    /// recreation between checks; callers must treat a failure as requiring manual inspection.
    /// </summary>
    public static void CopyFileForContainedPublication(
        string source,
        string destination,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var sourcePath = Path.GetFullPath(source);
        var directory = NormalizeDirectoryPath(destinationDirectory);
        var destinationPath = Path.GetFullPath(destination);
        if (!IsSafePathWithinDirectory(sourcePath, Path.GetPathRoot(sourcePath)!))
            throw new InvalidDataException("Source file contains a symbolic link, junction, or reparse point.");
        if (!IsPathWithinDirectory(destinationPath, directory))
            throw new InvalidDataException("Destination file is outside its publication directory.");
        var identity = CaptureDirectoryIdentity(directory);
        if (!IsSafePathWithinDirectory(destinationPath, directory))
            throw new InvalidDataException("Destination file contains a symbolic link, junction, or reparse point.");
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
            throw new IOException($"Destination already exists: {destinationPath}");

        string? temporary = null;
        var published = false;
        try
        {
            temporary = AtomicFile.CreateSiblingTemporaryPath(destinationPath);
            if (!IsSafePathWithinDirectory(temporary, directory))
                throw new InvalidDataException("Temporary publication path contains a symbolic link, junction, or reparse point.");
            using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!HasSameDirectoryIdentity(identity) || !IsSafePathWithinDirectory(temporary, directory) ||
                !IsSafePathWithinDirectory(destinationPath, directory))
                throw new InvalidDataException("Publication directory changed to a symbolic link, junction, reparse point, or different directory.");
            File.Move(temporary, destinationPath, overwrite: false);
            temporary = null;
            published = true;
            if (!HasSameDirectoryIdentity(identity) || !IsSafePathWithinDirectory(destinationPath, directory))
            {
                TryRollbackPublishedArtifact(destinationPath, directory);
                throw new InvalidDataException("Publication directory changed during file publication; the new artifact was rolled back when safe.");
            }
        }
        finally
        {
            if (temporary is not null)
            {
                try
                {
                    if (IsSafePathWithinDirectory(temporary, directory) && File.Exists(temporary)) File.Delete(temporary);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
            if (published)
            {
                try
                {
                    if (!HasSameDirectoryIdentity(identity) || !IsSafePathWithinDirectory(destinationPath, directory))
                        TryRollbackPublishedArtifact(destinationPath, directory);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) { }
            }
        }
    }

    /// <summary>
    /// Enumerates lexical descendants without asking the runtime to recurse. Reparse-point
    /// directories are skipped before descent and reparse-point files are excluded, preventing
    /// traversal through junctions, symbolic links, cycles, or paths outside <paramref name="root"/>.
    /// </summary>
    public static IEnumerable<string> EnumerateFilesRecursivelyWithoutReparsePoints(
        string root,
        string searchPattern = "*",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(searchPattern);
        var normalizedRoot = NormalizeDirectoryPath(root);
        if (!Directory.Exists(normalizedRoot)) throw new DirectoryNotFoundException(normalizedRoot);
        if (!HasNoReparsePointsInExistingPath(normalizedRoot))
            throw new InvalidDataException("Directory contains a symbolic link, junction, or reparse point.");

        var pending = new Stack<string>();
        var visited = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        pending.Push(normalizedRoot);
        visited.Add(normalizedRoot);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            if (!IsSafePathWithinDirectory(directory, normalizedRoot))
                throw new InvalidDataException("Directory changed to a symbolic link, junction, reparse point, or outside path while scanning.");

            foreach (var path in Directory.EnumerateFiles(directory, searchPattern, SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullPath = Path.GetFullPath(path);
                if (IsSafePathWithinDirectory(fullPath, normalizedRoot)) yield return fullPath;
            }

            foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullChild = NormalizeDirectoryPath(child);
                if (!IsSafePathWithinDirectory(fullChild, normalizedRoot)) continue;
                if (visited.Add(fullChild)) pending.Push(fullChild);
            }
        }
    }

    /// <summary>
    /// Captures the Windows file identity of an existing non-reparse directory. The identity is
    /// used to detect a parent-directory replacement between a publication preflight and commit.
    /// </summary>
    public static DirectoryIdentity CaptureDirectoryIdentity(string directory)
    {
        var fullDirectory = NormalizeDirectoryPath(directory);
        if (!Directory.Exists(fullDirectory)) throw new DirectoryNotFoundException(fullDirectory);
        if (!HasNoReparsePointsInExistingPath(fullDirectory))
            throw new InvalidDataException("Directory contains a symbolic link, junction, or reparse point.");

        var identity = CaptureDirectoryIdentityWithoutAncestorSafety(fullDirectory);
        if (!HasNoReparsePointsInExistingPath(fullDirectory))
            throw new InvalidDataException("Directory changed to a symbolic link, junction, or reparse point while inspecting it.");
        return identity;
    }

    private static DirectoryIdentity CaptureDirectoryIdentityWithoutAncestorSafety(string directory)
    {
        var fullDirectory = NormalizeDirectoryPath(directory);
        if (!Directory.Exists(fullDirectory)) throw new DirectoryNotFoundException(fullDirectory);

        if (!OperatingSystem.IsWindows())
        {
            var fallback = new DirectoryInfo(fullDirectory);
            return new DirectoryIdentity(fullDirectory, 0, (uint)fallback.CreationTimeUtc.Ticks, (uint)(fallback.CreationTimeUtc.Ticks >> 32));
        }

        using var handle = CreateFile(
            fullDirectory,
            FileReadAttributes,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException($"Cannot inspect directory identity: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        if (!GetFileInformationByHandle(handle, out var information))
            throw new IOException($"Cannot inspect directory identity: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        return new DirectoryIdentity(fullDirectory, information.VolumeSerialNumber, information.FileIndexHigh, information.FileIndexLow);
    }

    public static bool HasSameDirectoryIdentity(DirectoryIdentity expected) =>
        expected.Equals(CaptureDirectoryIdentity(expected.Path));

    public static bool IsReparsePoint(FileAttributes attributes) =>
        (attributes & FileAttributes.ReparsePoint) != 0;

    private static void TryRollbackPublishedArtifact(string destinationPath, string directory)
    {
        try
        {
            if (IsSafePathWithinDirectory(destinationPath, directory) && File.Exists(destinationPath))
                File.Delete(destinationPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private const int ErrorAlreadyExists = 183;
    private const uint FileReadAttributes = 0x80;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint FileShareDelete = 0x4;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string pathName, IntPtr securityAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public FileAttributes FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}

public readonly record struct DirectoryIdentity(string Path, uint VolumeSerialNumber, uint FileIndexHigh, uint FileIndexLow);
