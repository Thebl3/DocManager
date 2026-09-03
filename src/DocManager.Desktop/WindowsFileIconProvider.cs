using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;

namespace DocManager.Desktop;

internal static class WindowsFileIconProvider
{
    private const uint FileAttributeNormal = 0x00000080;
    private const uint ShgfiIcon = 0x00000100;
    private const uint ShgfiSmallIcon = 0x00000001;
    private const uint ShgfiUseFileAttributes = 0x00000010;
    private static readonly object Sync = new();
    private static readonly Dictionary<string, ImageSource?> FileIcons = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ImageSource?> AssociationIcons = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? GetSmallIcon(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return null;

        try
        {
            var normalizedPath = Path.GetFullPath(fullPath);
            lock (Sync)
            {
                if (FileIcons.TryGetValue(normalizedPath, out var cached)) return cached;

                var icon = ExtractIcon(normalizedPath, ShgfiIcon | ShgfiSmallIcon)
                    ?? GetAssociationIcon(Path.GetExtension(normalizedPath));
                FileIcons[normalizedPath] = icon;
                return icon;
            }
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? GetAssociationIcon(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return null;
        if (AssociationIcons.TryGetValue(extension, out var cached)) return cached;

        var icon = ExtractIcon(extension, ShgfiIcon | ShgfiSmallIcon | ShgfiUseFileAttributes);
        AssociationIcons[extension] = icon;
        return icon;
    }

    private static ImageSource? ExtractIcon(string path, uint flags)
    {
        var result = SHGetFileInfo(path, FileAttributeNormal, out var fileInfo, (uint)Marshal.SizeOf<ShFileInfo>(), flags);
        if (result == IntPtr.Zero || fileInfo.IconHandle == IntPtr.Zero) return null;

        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(fileInfo.IconHandle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
        finally
        {
            DestroyIcon(fileInfo.IconHandle);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint fileAttributes, out ShFileInfo fileInfo, uint fileInfoSize, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr iconHandle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }
}
