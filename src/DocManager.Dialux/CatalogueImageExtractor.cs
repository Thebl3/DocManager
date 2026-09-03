using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Annotations;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Images;
using UglyToad.PdfPig.Tokens;

namespace DocManager.Dialux;

public interface IPdfProductImageExtractor
{
    PdfProductImage? Extract(string pdfPath, CancellationToken cancellationToken = default);
}

public sealed record PdfProductImage(byte[] Png, double Aspect)
{
    internal PdfProductImageDiagnostics? Diagnostics { get; init; }
}

public sealed class PdfPigProductImageExtractor : IPdfProductImageExtractor
{
    private const int MinimumSampleDimension = 16;
    private const long MaximumSourcePixels = 100_000_000;
    private const int MaximumPngBytes = 16 * 1024 * 1024;
    private const int JpegDecodeDimensionLimit = 2_300;
    private const int MaximumPageAnnotations = 256;
    private const int DefaultMaximumCacheEntries = 128;
    private const long DefaultMaximumCacheBytes = 64L * 1024 * 1024;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<CacheEntry> _leastRecentlyUsed = new();
    private readonly int _maximumCacheEntries;
    private readonly long _maximumCacheBytes;
    private long _cachedImageBytes;

    public PdfPigProductImageExtractor(int maximumCacheEntries = DefaultMaximumCacheEntries, long maximumCacheBytes = DefaultMaximumCacheBytes)
    {
        if (maximumCacheEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCacheEntries));
        if (maximumCacheBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumCacheBytes));
        _maximumCacheEntries = maximumCacheEntries;
        _maximumCacheBytes = maximumCacheBytes;
    }

    public PdfProductImage? Extract(string pdfPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        cancellationToken.ThrowIfCancellationRequested();

        string fullPath;
        FileInfo file;
        try
        {
            fullPath = Path.GetFullPath(pdfPath);
            file = new FileInfo(fullPath);
            if (!file.Exists)
            {
                RemoveCached(fullPath);
                return null;
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        var stamp = new FileStamp(file.Length, file.LastWriteTimeUtc.Ticks);
        if (TryGetCached(fullPath, stamp, out var cached)) return cached;

        PdfProductImage? image;
        try
        {
            image = ExtractCore(fullPath, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            image = null;
        }

        AddCached(fullPath, stamp, image);
        return image;
    }

    private void RemoveCached(string path)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(path, out var node)) RemoveNode(node);
        }
    }

    private bool TryGetCached(string path, FileStamp stamp, out PdfProductImage? image)
    {
        lock (_cacheLock)
        {
            if (!_cache.TryGetValue(path, out var node))
            {
                image = null;
                return false;
            }

            if (node.Value.Stamp != stamp)
            {
                RemoveNode(node);
                image = null;
                return false;
            }

            _leastRecentlyUsed.Remove(node);
            _leastRecentlyUsed.AddLast(node);
            image = node.Value.Image;
            return true;
        }
    }

    private void AddCached(string path, FileStamp stamp, PdfProductImage? image)
    {
        var imageBytes = image?.Png.LongLength ?? 0;
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(path, out var previous)) RemoveNode(previous);
            if (imageBytes > _maximumCacheBytes) return;

            var node = _leastRecentlyUsed.AddLast(new CacheEntry(path, stamp, image, imageBytes));
            _cache[path] = node;
            _cachedImageBytes += imageBytes;
            while (_cache.Count > _maximumCacheEntries || _cachedImageBytes > _maximumCacheBytes)
            {
                var oldest = _leastRecentlyUsed.First;
                if (oldest is null) break;
                RemoveNode(oldest);
            }
        }
    }

    private void RemoveNode(LinkedListNode<CacheEntry> node)
    {
        _leastRecentlyUsed.Remove(node);
        _cache.Remove(node.Value.Path);
        _cachedImageBytes -= node.Value.ImageBytes;
    }

    internal (int Entries, long ImageBytes) CacheUsage
    {
        get
        {
            lock (_cacheLock) return (_cache.Count, _cachedImageBytes);
        }
    }

    private static PdfProductImage? ExtractCore(string path, CancellationToken cancellationToken)
    {
        using var document = PdfDocument.Open(path);
        if (document.NumberOfPages == 0) return null;
        var page = document.GetPage(1);
        var selectionRegion = TryGetReferenceImageRegion(page);
        var images = OrderCandidates(page, selectionRegion).ToArray();

        foreach (var image in images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var raster = GetPng(image, cancellationToken);
                if (raster is null) continue;
                var cropped = AdaptiveImageCropper.CropAndLimit(raster.Png, raster.Width, raster.Height, cancellationToken);
                if (cropped is null) continue;
                if (selectionRegion.HasValue && cropped.Diagnostics is not null)
                {
                    cropped = cropped with { Diagnostics = cropped.Diagnostics with { AnnotationGuided = true } };
                }
                return cropped;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // A malformed or unsupported candidate must not prevent trying the next page-one raster.
            }
        }

        return null;
    }

    private static IEnumerable<IPdfImage> OrderCandidates(Page page, PdfRectangle? selectionRegion)
    {
        var candidates = page.GetImages()
            .Where(image => !image.IsImageMask
                && image.WidthInSamples >= MinimumSampleDimension
                && image.HeightInSamples >= MinimumSampleDimension
                && (long)image.WidthInSamples * image.HeightInSamples <= MaximumSourcePixels
                && !IsFullPageArtwork(image, page.Width, page.Height))
            .ToArray();

        return selectionRegion.HasValue
            ? candidates.OrderByDescending(image => SelectionScore(image.Bounds, selectionRegion.Value))
                .ThenBy(image => IsHeaderBranding(image, page.Width, page.Height))
                .ThenByDescending(DisplayedArea)
                .ThenByDescending(SampleArea)
            : candidates.OrderBy(image => IsHeaderBranding(image, page.Width, page.Height))
                .ThenBy(image => image.MaskImage is not null)
                .ThenByDescending(DisplayedArea)
                .ThenByDescending(SampleArea);
    }

    private static PdfRectangle? TryGetReferenceImageRegion(Page page)
    {
        try
        {
            var annotations = page.GetAnnotations().Take(MaximumPageAnnotations).ToArray();
            var reference = annotations.FirstOrDefault(annotation =>
                annotation.Type == AnnotationType.FreeText && IsReferenceImageText(annotation.Content));
            if (reference is null) return null;

            var redSquares = annotations
                .Where(annotation => annotation.Type == AnnotationType.Square && IsRedSquare(annotation))
                .Select(annotation => annotation.Rectangle)
                .ToArray();
            return SelectNearestSquare(reference.Rectangle, redSquares);
        }
        catch
        {
            // Annotation guidance is optional. PdfPig candidate heuristics remain the safe fallback.
            return null;
        }
    }

    private static bool IsRedSquare(Annotation annotation)
    {
        if (!annotation.AnnotationDictionary.TryGet<ArrayToken>(NameToken.C, out var color) || color.Length != 3) return false;
        var components = color.Data.OfType<NumericToken>().Select(component => component.Double).ToArray();
        if (components.Length != 3 || components.Any(component => !double.IsFinite(component) || component < 0 || component > 1)) return false;
        return components[0] >= .70
            && components[1] <= .35
            && components[2] <= .35
            && components[0] - Math.Max(components[1], components[2]) >= .40;
    }

    internal static PdfRectangle? SelectReferenceImageRegion(
        IEnumerable<(AnnotationType Type, string? Content, PdfRectangle Rectangle, bool IsRed)> annotations)
    {
        var materialized = annotations.Take(MaximumPageAnnotations).ToArray();
        var reference = materialized.FirstOrDefault(annotation =>
            annotation.Type == AnnotationType.FreeText && IsReferenceImageText(annotation.Content));
        if (reference.Type != AnnotationType.FreeText || !IsReferenceImageText(reference.Content)) return null;
        var redSquares = materialized
            .Where(annotation => annotation.Type == AnnotationType.Square && annotation.IsRed)
            .Select(annotation => annotation.Rectangle)
            .ToArray();
        return SelectNearestSquare(reference.Rectangle, redSquares);
    }

    private static PdfRectangle? SelectNearestSquare(PdfRectangle reference, IReadOnlyList<PdfRectangle> squares) =>
        squares.Count == 0 ? null : squares.OrderBy(square => RectangleDistance(reference, square)).First();

    private static bool IsReferenceImageText(string? text) => NormalizeAnnotationText(text) == "reference image";

    private static string NormalizeAnnotationText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return Regex.Replace(text.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant(), @"\s+", " ");
    }

    internal static int SelectAnnotationGuidedCandidateIndex(IReadOnlyList<PdfRectangle> candidates, PdfRectangle selector)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates
            .Select((bounds, index) => (bounds, index))
            .OrderByDescending(candidate => SelectionScore(candidate.bounds, selector))
            .ThenByDescending(candidate => RectangleArea(candidate.bounds))
            .ThenBy(candidate => candidate.index)
            .Select(candidate => candidate.index)
            .DefaultIfEmpty(-1)
            .First();
    }

    private static double SelectionScore(PdfRectangle image, PdfRectangle selector)
    {
        var overlap = IntersectionArea(image, selector);
        if (overlap > 0)
        {
            var union = RectangleArea(image) + RectangleArea(selector) - overlap;
            if (double.IsFinite(union) && union > 0)
            {
                var intersectionOverUnion = overlap / union;
                if (double.IsFinite(intersectionOverUnion) && intersectionOverUnion >= 0)
                {
                    return 1_000_000d + Math.Clamp(intersectionOverUnion, 0d, 1d);
                }
            }
        }

        var distance = RectangleDistance(image, selector);
        return double.IsFinite(distance) && distance >= 0 ? 1d / (1d + distance) : 0d;
    }

    private static double IntersectionArea(PdfRectangle left, PdfRectangle right)
    {
        if (!HasFiniteBounds(left) || !HasFiniteBounds(right)) return 0d;
        var width = Math.Max(0, Math.Min(left.Right, right.Right) - Math.Max(left.Left, right.Left));
        var height = Math.Max(0, Math.Min(left.Top, right.Top) - Math.Max(left.Bottom, right.Bottom));
        var area = width * height;
        return double.IsFinite(area) && area > 0 ? area : 0d;
    }

    private static double RectangleArea(PdfRectangle rectangle)
    {
        if (!HasFiniteBounds(rectangle)) return 0d;
        var area = Math.Abs(rectangle.Width) * Math.Abs(rectangle.Height);
        return double.IsFinite(area) && area > 0 ? area : 0d;
    }

    private static double RectangleDistance(PdfRectangle left, PdfRectangle right)
    {
        if (!HasFiniteBounds(left) || !HasFiniteBounds(right)) return double.PositiveInfinity;
        var horizontal = Math.Max(0, Math.Max(left.Left - right.Right, right.Left - left.Right));
        var vertical = Math.Max(0, Math.Max(left.Bottom - right.Top, right.Bottom - left.Top));
        var squaredDistance = horizontal * horizontal + vertical * vertical;
        return double.IsFinite(squaredDistance) && squaredDistance >= 0 ? Math.Sqrt(squaredDistance) : double.PositiveInfinity;
    }

    private static bool HasFiniteBounds(PdfRectangle rectangle) =>
        double.IsFinite(rectangle.Left)
        && double.IsFinite(rectangle.Right)
        && double.IsFinite(rectangle.Bottom)
        && double.IsFinite(rectangle.Top);

    private static ExtractedRaster? GetPng(IPdfImage image, CancellationToken cancellationToken)
    {
        if (image.TryGetPng(out var png)
            && png.Length is > 0 and <= MaximumPngBytes
            && AdaptiveImageCropper.TryGetPngDimensions(png, out var pngWidth, out var pngHeight)
            && pngWidth == image.WidthInSamples
            && pngHeight == image.HeightInSamples)
        {
            return new ExtractedRaster(png, pngWidth, pngHeight);
        }

        var raw = image.RawMemory;
        if (raw.Length is > 0 and <= MaximumPngBytes
            && TryReadJpegDimensions(raw.Span, out var jpegWidth, out var jpegHeight)
            && jpegWidth == image.WidthInSamples
            && jpegHeight == image.HeightInSamples)
        {
            var jpegRaster = ConvertJpegToPng(raw, jpegWidth, jpegHeight, cancellationToken);
            if (jpegRaster is not null) return jpegRaster;
        }

        if (image.BitsPerComponent != 8 || image.ColorSpaceDetails is null || !image.TryGetBytesAsMemory(out var decoded)) return null;

        var bytes = decoded.ToArray();
        var components = image.ColorSpaceDetails.NumberOfColorComponents;
        var expectedBytes = checked(image.WidthInSamples * image.HeightInSamples * components);
        if (components is not (1 or 3 or 4) || bytes.Length < expectedBytes || expectedBytes > MaximumSourcePixels * 4) return null;
        ColorSpaceDetailsByteConverter.Convert(image.ColorSpaceDetails, bytes, image.WidthInSamples, image.HeightInSamples, image.BitsPerComponent);

        PixelFormat format;
        if (components == 1) format = PixelFormats.Gray8;
        else if (components == 3) format = PixelFormats.Rgb24;
        else
        {
            var bgra = new byte[checked(image.WidthInSamples * image.HeightInSamples * 4)];
            for (var source = 0; source < bgra.Length; source += 4)
            {
                bgra[source] = bytes[source + 2];
                bgra[source + 1] = bytes[source + 1];
                bgra[source + 2] = bytes[source];
                bgra[source + 3] = bytes[source + 3];
            }
            bytes = bgra;
            format = PixelFormats.Bgra32;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var stride = checked(image.WidthInSamples * format.BitsPerPixel / 8);
        var bitmap = BitmapSource.Create(image.WidthInSamples, image.HeightInSamples, 96, 96, format, null, bytes, stride);
        bitmap.Freeze();
        var encoded = EncodePng(bitmap);
        return encoded is null ? null : new ExtractedRaster(encoded, bitmap.PixelWidth, bitmap.PixelHeight);
    }

    private static ExtractedRaster? ConvertJpegToPng(Memory<byte> jpeg, int width, int height, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var input = new MemoryStream(jpeg.ToArray(), writable: false);
        var decoded = new BitmapImage();
        decoded.BeginInit();
        decoded.CacheOption = BitmapCacheOption.OnLoad;
        decoded.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        decoded.StreamSource = input;
        if (Math.Max(width, height) > JpegDecodeDimensionLimit)
        {
            if (width >= height) decoded.DecodePixelWidth = JpegDecodeDimensionLimit;
            else decoded.DecodePixelHeight = JpegDecodeDimensionLimit;
        }
        decoded.EndInit();
        decoded.Freeze();
        if (decoded.PixelWidth < MinimumSampleDimension || decoded.PixelHeight < MinimumSampleDimension
            || (long)decoded.PixelWidth * decoded.PixelHeight > MaximumSourcePixels) return null;

        cancellationToken.ThrowIfCancellationRequested();
        var png = EncodePng(decoded);
        return png is null ? null : new ExtractedRaster(png, decoded.PixelWidth, decoded.PixelHeight);
    }

    private static byte[]? EncodePng(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.Length is > 0 and <= MaximumPngBytes ? output.ToArray() : null;
    }

    private static bool TryReadJpegDimensions(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (bytes.Length < 4 || bytes[0] != 0xff || bytes[1] != 0xd8) return false;

        var offset = 2;
        while (offset < bytes.Length)
        {
            while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
            if (offset >= bytes.Length) return false;
            var marker = bytes[offset++];
            if (marker is 0xd8 or 0xd9 || marker == 0x01 || marker is >= 0xd0 and <= 0xd7) continue;
            if (offset + 2 > bytes.Length) return false;
            var segmentLength = (bytes[offset] << 8) | bytes[offset + 1];
            if (segmentLength < 2 || offset + segmentLength > bytes.Length) return false;

            if (IsStartOfFrame(marker))
            {
                if (segmentLength < 8) return false;
                height = (bytes[offset + 3] << 8) | bytes[offset + 4];
                width = (bytes[offset + 5] << 8) | bytes[offset + 6];
                return width >= MinimumSampleDimension
                    && height >= MinimumSampleDimension
                    && (long)width * height <= MaximumSourcePixels;
            }

            if (marker == 0xda) return false;
            offset += segmentLength;
        }

        return false;
    }

    private static bool IsStartOfFrame(byte marker) => marker is
        0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf;

    private static bool IsHeaderBranding(IPdfImage image, double pageWidth, double pageHeight) =>
        image.Bounds.Right <= pageWidth * .45 && image.Bounds.Top >= pageHeight * .70;

    private static bool IsFullPageArtwork(IPdfImage image, double pageWidth, double pageHeight)
    {
        if (pageWidth <= 0 || pageHeight <= 0) return true;
        var widthRatio = Math.Abs(image.Bounds.Width) / pageWidth;
        var heightRatio = Math.Abs(image.Bounds.Height) / pageHeight;
        var areaRatio = DisplayedArea(image) / (pageWidth * pageHeight);
        return (widthRatio >= .90 && heightRatio >= .90) || areaRatio >= .75;
    }

    private static double DisplayedArea(IPdfImage image) => Math.Abs(image.Bounds.Width * image.Bounds.Height);
    private static long SampleArea(IPdfImage image) => (long)image.WidthInSamples * image.HeightInSamples;

    private readonly record struct FileStamp(long Length, long LastWriteTicks);
    private sealed record CacheEntry(string Path, FileStamp Stamp, PdfProductImage? Image, long ImageBytes);
    private sealed record ExtractedRaster(byte[] Png, int Width, int Height);
}
