using System.IO;
using DocManager.Core;

namespace DocManager.Desktop;

internal static class DesktopExportPaths
{
    public static IReadOnlyList<(string SourcePath, string OutputPath)> ForSeparateExports(
        IEnumerable<string> sourcePaths,
        string? selectedOutputPath)
    {
        var sources = sourcePaths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var selectedDirectory = string.IsNullOrWhiteSpace(selectedOutputPath)
            ? null
            : Path.GetDirectoryName(Path.GetFullPath(selectedOutputPath));
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(string SourcePath, string OutputPath)>(sources.Length);

        foreach (var source in sources)
        {
            var directory = selectedDirectory ?? Path.GetDirectoryName(source)!;
            var stem = WindowsFileName.Sanitize(Path.GetFileNameWithoutExtension(source));
            var candidate = Path.Combine(directory, $"{stem}.xlsx");
            if (File.Exists(candidate) || !reserved.Add(candidate))
            {
                var parent = WindowsFileName.Sanitize(new DirectoryInfo(Path.GetDirectoryName(source)!).Name);
                candidate = UniquePath(directory, $"{stem} - {parent}", reserved);
            }

            result.Add((source, candidate));
        }

        return result;
    }

    private static string UniquePath(string directory, string stem, ISet<string> reserved)
    {
        for (var index = 1; ; index++)
        {
            var suffix = index == 1 ? string.Empty : $" ({index})";
            var candidate = Path.Combine(directory, $"{stem}{suffix}.xlsx");
            if (!File.Exists(candidate) && reserved.Add(candidate)) return candidate;
        }
    }
}
