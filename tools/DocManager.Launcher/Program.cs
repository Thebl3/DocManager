using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class Program
{
    private const string LauncherMarker = "DocManager.ManagedLauncher.v2";
    private const string ProbeSwitch = "--docmanager-launcher-probe";
    private const int LauncherFailureExitCode = 100;
    private const int ProbeSuccessExitCode = 73;

    [STAThread]
    private static int Main()
    {
        try
        {
            string launcherPath = GetLauncherPath();
            string launcherDirectory = Path.GetDirectoryName(launcherPath)
                ?? throw new InvalidOperationException("Cannot determine the DocManager launcher directory.");
            string releasedDirectory = Path.GetFullPath(Path.Combine(launcherDirectory, "Released"));
            string targetPath = Path.GetFullPath(Path.Combine(releasedDirectory, "DocManager.Desktop.exe"));

            if (PathsReferToSameFile(launcherPath, targetPath))
            {
                return Fail("The DocManager launcher target resolves to the launcher itself. Keep the real application under Released\\DocManager.Desktop.exe.");
            }
            if (File.Exists(targetPath) && PathsReferToSameFileIdentity(launcherPath, targetPath))
            {
                return Fail("The DocManager launcher target points back to the launcher. Replace Released\\DocManager.Desktop.exe with the real application payload.");
            }

            string[] arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
            if (arguments.Length > 0 && arguments[0].Equals(ProbeSwitch, StringComparison.Ordinal))
            {
                return RunProbe(arguments, launcherPath, launcherDirectory, releasedDirectory, targetPath);
            }

            if (!File.Exists(targetPath))
            {
                return Fail($"DocManager cannot start because the application payload is missing.\n\nExpected file:\n{targetPath}\n\nCopy or rebuild the complete App folder so Released remains beside this launcher.");
            }

            ProcessStartInfo startInfo = new()
            {
                FileName = targetPath,
                WorkingDirectory = releasedDirectory,
                UseShellExecute = false,
            };
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using Process child = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows did not return a process for Released\\DocManager.Desktop.exe.");
            child.WaitForExit();
            return child.ExitCode;
        }
        catch (Exception exception)
        {
            return Fail($"DocManager could not start.\n\n{exception.Message}\n\nRebuild or copy the complete App folder, then try again.");
        }
    }

    private static int RunProbe(
        string[] arguments,
        string launcherPath,
        string launcherDirectory,
        string releasedDirectory,
        string targetPath)
    {
        if (arguments.Length != 2 || string.IsNullOrWhiteSpace(arguments[1]))
        {
            return LauncherFailureExitCode;
        }

        string markerPath = Path.GetFullPath(arguments[1]);
        string? markerDirectory = Path.GetDirectoryName(markerPath);
        if (string.IsNullOrWhiteSpace(markerDirectory) ||
            !Directory.Exists(markerDirectory) ||
            !IsSameOrChildPath(markerPath, Path.GetTempPath()))
        {
            return LauncherFailureExitCode;
        }

        var marker = new
        {
            marker = LauncherMarker,
            processPath = launcherPath,
            baseDirectory = EnsureTrailingSeparator(Path.GetFullPath(AppContext.BaseDirectory)),
            launcherDirectory,
            releasedDirectory,
            targetPath,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            is64BitProcess = Environment.Is64BitProcess,
        };
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(marker);
        using FileStream stream = new(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(json);
        stream.Flush(flushToDisk: true);
        return ProbeSuccessExitCode;
    }

    private static string GetLauncherPath()
    {
        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            return Path.GetFullPath(processPath);
        }

        string commandPath = Environment.GetCommandLineArgs().FirstOrDefault()
            ?? throw new InvalidOperationException("Cannot locate the DocManager launcher executable.");
        return Path.GetFullPath(commandPath, AppContext.BaseDirectory);
    }

    private static bool PathsReferToSameFile(string first, string second)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Equals(
                Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                comparison);
    }

    private static bool IsSameOrChildPath(string candidate, string root)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string candidatePath = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return candidatePath.Equals(rootPath, comparison) ||
            candidatePath.StartsWith(rootPath + Path.DirectorySeparatorChar, comparison);
    }

    private static bool PathsReferToSameFileIdentity(string first, string second)
    {
        using FileStream firstStream = new(first, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using FileStream secondStream = new(second, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(firstStream.SafeFileHandle, out ByHandleFileInformation firstInfo) ||
            !GetFileInformationByHandle(secondStream.SafeFileHandle, out ByHandleFileInformation secondInfo))
        {
            return false;
        }

        return firstInfo.VolumeSerialNumber == secondInfo.VolumeSerialNumber &&
            firstInfo.FileIndexHigh == secondInfo.FileIndexHigh &&
            firstInfo.FileIndexLow == secondInfo.FileIndexLow;
    }

    private static string EnsureTrailingSeparator(string path) =>
        Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;

    private static int Fail(string message)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("DOCMANAGER_LAUNCHER_NO_UI"), "1", StringComparison.Ordinal))
        {
            MessageBoxW(IntPtr.Zero, message, "DocManager", 0x00000010u | 0x00010000u);
        }
        return LauncherFailureExitCode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        Microsoft.Win32.SafeHandles.SafeFileHandle fileHandle,
        out ByHandleFileInformation fileInformation);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr windowHandle, string text, string caption, uint type);
}
