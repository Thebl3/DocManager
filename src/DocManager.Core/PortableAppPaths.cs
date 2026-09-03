namespace DocManager.Core;

public static class PortableAppPaths
{
    public const string ReleasedDirectoryName = "Released";
    public const string ProductFamilyDirectoryName = "Product Family";
    public const string ProductExcelDirectoryName = "Product excel file";
    public const string LauncherFileName = "DocManager.Desktop.exe";
    public const string PublishManifestFileName = ".docmanager-publish-manifest.json";

    public static string ResolveRoot(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var normalizedBase = Path.GetFullPath(baseDirectory);
        var normalizedDirectory = normalizedBase.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var leaf = Path.GetFileName(normalizedDirectory);
        if (!leaf.Equals(ReleasedDirectoryName, StringComparison.OrdinalIgnoreCase))
            return normalizedBase;

        var parent = Directory.GetParent(normalizedDirectory)?.FullName;
        if (parent is null)
            return normalizedBase;

        var hasPortableLayout =
            Directory.Exists(Path.Combine(parent, ProductFamilyDirectoryName)) ||
            Directory.Exists(Path.Combine(parent, ProductExcelDirectoryName)) ||
            File.Exists(Path.Combine(parent, LauncherFileName)) ||
            File.Exists(Path.Combine(parent, PublishManifestFileName));

        return hasPortableLayout ? Path.GetFullPath(parent) : normalizedBase;
    }
}
