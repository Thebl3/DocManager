using DocManager.Core;

namespace DocManager.Desktop;

public static class DesktopCataloguePaths
{
    public static string DefaultProductFamilyRoot(string? baseDirectory = null) =>
        System.IO.Path.Combine(
            PortableAppPaths.ResolveRoot(baseDirectory ?? AppContext.BaseDirectory),
            PortableAppPaths.ProductFamilyDirectoryName);
}
