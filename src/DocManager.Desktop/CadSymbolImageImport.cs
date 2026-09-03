using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clipboard = System.Windows.Clipboard;

namespace DocManager.Desktop;

internal interface ICadSymbolClipboard
{
    IReadOnlyList<string> GetFileDropList();
    BitmapSource? GetImage();
}

internal sealed class WpfCadSymbolClipboard : ICadSymbolClipboard
{
    public IReadOnlyList<string> GetFileDropList() => Clipboard.ContainsFileDropList()
        ? Clipboard.GetFileDropList().Cast<string>().ToArray()
        : [];
    public BitmapSource? GetImage() => Clipboard.ContainsImage() ? Clipboard.GetImage() : null;
}

internal sealed record CanonicalCadSymbolImage(byte[] PngBytes, int PixelWidth, int PixelHeight, string Source);

internal static class CadSymbolImageImport
{
    internal const long MaximumSourceBytes = 32L * 1024 * 1024;
    internal const int MaximumSourceDimension = 8192;
    internal const long MaximumSourcePixels = 40_000_000;
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"
    };

    internal static CanonicalCadSymbolImage FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) || !SupportedExtensions.Contains(Path.GetExtension(fullPath)))
            throw new InvalidDataException("Chỉ hỗ trợ PNG/JPG/JPEG/BMP/GIF/TIFF hợp lệ.");
        var length = new FileInfo(fullPath).Length;
        if (length <= 0 || length > MaximumSourceBytes)
            throw new InvalidDataException("Tệp ảnh rỗng hoặc vượt giới hạn 32 MiB.");
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return DecodeAndEncode(stream, $"file:{Path.GetFileName(fullPath)}");
    }

    internal static CanonicalCadSymbolImage FromClipboard(ICadSymbolClipboard clipboard)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        IReadOnlyList<string> dropped = RetryClipboard(clipboard.GetFileDropList);
        if (dropped.Count > 0)
        {
            if (dropped.Count != 1) throw new InvalidDataException("Clipboard phải chứa đúng một tệp ảnh.");
            return FromFile(dropped[0]);
        }
        var image = RetryClipboard(clipboard.GetImage);
        if (image is null) throw new InvalidDataException("Clipboard không có ảnh hoặc một tệp ảnh được hỗ trợ.");
        return EncodeBitmap(image, "clipboard");
    }

    internal static async Task<CanonicalCadSymbolImage> FromClipboardAsync(ICadSymbolClipboard clipboard)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        IReadOnlyList<string> dropped = await RetryClipboardAsync(clipboard.GetFileDropList);
        if (dropped.Count > 0)
        {
            if (dropped.Count != 1) throw new InvalidDataException("Clipboard phải chứa đúng một tệp ảnh.");
            return await Task.Run(() => FromFile(dropped[0]));
        }
        var image = await RetryClipboardAsync(clipboard.GetImage);
        if (image is null) throw new InvalidDataException("Clipboard không có ảnh hoặc một tệp ảnh được hỗ trợ.");
        return await Task.Run(() => EncodeBitmap(image, "clipboard"));
    }

    internal static CanonicalCadSymbolImage DecodeAndEncode(Stream source, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.CanSeek && (source.Length <= 0 || source.Length > MaximumSourceBytes))
            throw new InvalidDataException("Dữ liệu ảnh rỗng hoặc vượt giới hạn 32 MiB.");
        try
        {
            var decoder = BitmapDecoder.Create(source, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) throw new InvalidDataException("Ảnh không có frame hợp lệ.");
            return EncodeBitmap(decoder.Frames[0], sourceName);
        }
        catch (Exception exception) when (exception is NotSupportedException or FileFormatException or ArgumentException or IOException)
        {
            throw new InvalidDataException("Không giải mã được ảnh CAD symbol.", exception);
        }
    }

    internal static CanonicalCadSymbolImage EncodeBitmap(BitmapSource source, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateDimensions(source.PixelWidth, source.PixelHeight);
        BitmapSource canonical = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        canonical.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(canonical));
        using var output = new MemoryStream();
        encoder.Save(output);
        if (output.Length <= 0 || output.Length > DocManager.Core.CadSymbolRepository.MaximumImageBytes)
            throw new InvalidDataException("PNG sau mã hóa vượt giới hạn 16 MiB.");
        return new CanonicalCadSymbolImage(output.ToArray(), canonical.PixelWidth, canonical.PixelHeight, sourceName);
    }

    private static void ValidateDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > MaximumSourceDimension || height > MaximumSourceDimension ||
            (long)width * height > MaximumSourcePixels)
            throw new InvalidDataException("Ảnh vượt giới hạn 8192 px hoặc 40 triệu pixel.");
    }

    private static T RetryClipboard<T>(Func<T> action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return action(); }
            catch (System.Runtime.InteropServices.ExternalException) when (attempt < 2)
            {
                Thread.Sleep(60 * (attempt + 1));
            }
        }
    }

    private static async Task<T> RetryClipboardAsync<T>(Func<T> action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return action(); }
            catch (System.Runtime.InteropServices.ExternalException) when (attempt < 2)
            {
                await Task.Delay(60 * (attempt + 1));
            }
        }
    }
}
