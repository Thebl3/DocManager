using System.Buffers;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DocManager.Dialux;

internal static class AdaptiveImageCropper
{
    private const int MinimumDimension = 16;
    private const long MaximumPixels = 100_000_000;
    private const int DecodeDimensionLimit = 2_300;
    private const int OutputDimensionLimit = 1_600;
    private const int MaximumPngBytes = 16 * 1024 * 1024;
    private const int MaximumBorderSamples = 50_000;
    private const double BorderBandRatio = .03;
    private const double BackgroundDistancePercentile = .90;
    private const int BackgroundDistanceSafety = 5;
    private const int MinimumColorDistance = 10;
    private const int MaximumColorDistance = 26;
    private const int MinimumPaddingPixels = 8;
    private const double RelativePadding = .05;
    private const double MaximumForegroundRatio = .92;
    private const double MaximumUsefulCropAreaRatio = .985;
    private const double MinimumSupportedBackgroundRatio = .55;
    private const double MaximumBorderForegroundRatio = .22;
    private const double MinimumSignificantComponentRatio = .000008;
    private const double RelativeSignificantComponentRatio = .001;

    internal static PdfProductImage? CropAndLimit(byte[] sourceImage, int sourceWidth, int sourceHeight, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceImage);
        if (sourceImage.Length == 0 || sourceImage.Length > MaximumPngBytes) return null;
        if (sourceWidth < MinimumDimension || sourceHeight < MinimumDimension
            || (long)sourceWidth * sourceHeight > MaximumPixels) return null;
        if (!TryGetPngDimensions(sourceImage, out var encodedWidth, out var encodedHeight)
            || encodedWidth != sourceWidth
            || encodedHeight != sourceHeight) return null;

        cancellationToken.ThrowIfCancellationRequested();
        using var input = new MemoryStream(sourceImage, writable: false);
        var decoded = new BitmapImage();
        decoded.BeginInit();
        decoded.CacheOption = BitmapCacheOption.OnLoad;
        decoded.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        decoded.StreamSource = input;
        if (Math.Max(sourceWidth, sourceHeight) > DecodeDimensionLimit)
        {
            if (sourceWidth >= sourceHeight) decoded.DecodePixelWidth = DecodeDimensionLimit;
            else decoded.DecodePixelHeight = DecodeDimensionLimit;
        }
        decoded.EndInit();
        decoded.Freeze();

        if (decoded.PixelWidth < MinimumDimension || decoded.PixelHeight < MinimumDimension
            || (long)decoded.PixelWidth * decoded.PixelHeight > MaximumPixels) return null;

        var crop = FindForegroundCrop(decoded, cancellationToken);
        BitmapSource output = crop is not null ? new CroppedBitmap(decoded, crop.Bounds) : decoded;
        output.Freeze();
        var largest = Math.Max(output.PixelWidth, output.PixelHeight);
        if (largest > OutputDimensionLimit)
        {
            var scale = (double)OutputDimensionLimit / largest;
            output = new TransformedBitmap(output, new ScaleTransform(scale, scale));
            output.Freeze();
        }

        cancellationToken.ThrowIfCancellationRequested();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(output));
        using var result = new MemoryStream();
        encoder.Save(result);
        if (result.Length == 0 || result.Length > MaximumPngBytes) return null;

        var bounds = crop?.Bounds ?? new Int32Rect(0, 0, decoded.PixelWidth, decoded.PixelHeight);
        return new PdfProductImage(result.ToArray(), (double)output.PixelWidth / output.PixelHeight)
        {
            Diagnostics = new PdfProductImageDiagnostics(
                decoded.PixelWidth,
                decoded.PixelHeight,
                bounds,
                crop?.ForegroundBounds,
                crop?.ForegroundRatio ?? 0,
                crop?.BackgroundKind ?? "uncertain")
        };
    }

    internal static bool TryGetPngDimensions(ReadOnlySpan<byte> png, out int width, out int height)
    {
        width = 0;
        height = 0;
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (png.Length < 24 || !png[..8].SequenceEqual(signature)
            || !png.Slice(12, 4).SequenceEqual("IHDR"u8)) return false;

        var parsedWidth = ReadBigEndianInt32(png.Slice(16, 4));
        var parsedHeight = ReadBigEndianInt32(png.Slice(20, 4));
        if (parsedWidth < MinimumDimension || parsedHeight < MinimumDimension
            || (long)parsedWidth * parsedHeight > MaximumPixels) return false;
        width = parsedWidth;
        height = parsedHeight;
        return true;
    }

    private static int ReadBigEndianInt32(ReadOnlySpan<byte> bytes) =>
        (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];

    private static ForegroundCrop? FindForegroundCrop(BitmapSource source, CancellationToken cancellationToken)
    {
        var converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();

        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        converted.CopyPixels(pixels, stride, 0);

        var background = EstimateBackground(pixels, width, height, stride);
        if (background is null) return null;

        var mask = new byte[checked(width * height)];
        var foregroundPixels = 0;
        var supportedBackgroundPixels = 0;
        var borderForegroundPixels = 0;
        var borderPixels = 0;
        var confidenceStep = Math.Max(1, Math.Min(width, height) / 320);
        for (var y = 0; y < height; y++)
        {
            if ((y & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
            var pixelOffset = y * stride;
            var maskOffset = y * width;
            for (var x = 0; x < width; x++, pixelOffset += 4)
            {
                var foreground = IsForeground(pixels, pixelOffset, background);
                if (foreground)
                {
                    mask[maskOffset + x] = 1;
                    foregroundPixels++;
                }

                if (x % confidenceStep != 0 || y % confidenceStep != 0) continue;
                if (!foreground || IsStrongBackground(pixels, pixelOffset, background)) supportedBackgroundPixels++;
                if (!IsBorderSample(x, y, width, height, background.BorderWidth)) continue;
                borderPixels++;
                if (foreground) borderForegroundPixels++;
            }
        }

        var confidenceSamples = ((height - 1) / confidenceStep + 1) * ((width - 1) / confidenceStep + 1);
        if (!background.Transparent && (double)supportedBackgroundPixels / confidenceSamples < MinimumSupportedBackgroundRatio) return null;
        if (borderPixels == 0 || (double)borderForegroundPixels / borderPixels > MaximumBorderForegroundRatio) return null;

        foregroundPixels = RemoveIsolatedPixels(mask, width, height, cancellationToken);
        var totalPixels = checked(width * height);
        if (foregroundPixels < Math.Max(16, totalPixels / 100_000) || foregroundPixels > totalPixels * MaximumForegroundRatio) return null;

        var components = FindComponents(mask, width, height, cancellationToken);
        if (components.Count == 0) return null;
        var largestComponent = components.Max(component => component.Count);
        var significanceThreshold = Math.Max(
            12,
            Math.Max(
                (int)Math.Ceiling(totalPixels * MinimumSignificantComponentRatio),
                (int)Math.Ceiling(largestComponent * RelativeSignificantComponentRatio)));
        var significant = components.Where(component => IsSignificant(component, significanceThreshold)).ToArray();
        if (significant.Length == 0) return null;

        var minX = significant.Min(component => component.MinX);
        var minY = significant.Min(component => component.MinY);
        var maxX = significant.Max(component => component.MaxX);
        var maxY = significant.Max(component => component.MaxY);
        var retainedCount = significant.Sum(component => component.Count);
        var retainedRatio = (double)retainedCount / totalPixels;
        if (retainedRatio < .00001 || retainedRatio > MaximumForegroundRatio) return null;

        var contentWidth = maxX - minX + 1;
        var contentHeight = maxY - minY + 1;
        var paddingX = Math.Max(MinimumPaddingPixels, (int)Math.Ceiling(contentWidth * RelativePadding));
        var paddingY = Math.Max(MinimumPaddingPixels, (int)Math.Ceiling(contentHeight * RelativePadding));
        var left = Math.Max(0, minX - paddingX);
        var top = Math.Max(0, minY - paddingY);
        var right = Math.Min(width - 1, maxX + paddingX);
        var bottom = Math.Min(height - 1, maxY + paddingY);
        var cropWidth = right - left + 1;
        var cropHeight = bottom - top + 1;
        if (cropWidth < MinimumDimension || cropHeight < MinimumDimension) return null;

        var cropAreaRatio = (double)cropWidth * cropHeight / totalPixels;
        if (cropAreaRatio >= MaximumUsefulCropAreaRatio) return null;

        return new ForegroundCrop(
            new Int32Rect(left, top, cropWidth, cropHeight),
            new Int32Rect(minX, minY, contentWidth, contentHeight),
            retainedRatio,
            background.Transparent ? "transparent" : "near-white");
    }

    private static bool IsForeground(byte[] pixels, int offset, BackgroundModel background)
    {
        var alpha = pixels[offset + 3];
        if (background.Transparent) return alpha > background.AlphaThreshold;
        if (alpha < 224) return true;

        var blue = pixels[offset];
        var green = pixels[offset + 1];
        var red = pixels[offset + 2];
        var deltaBlue = blue - background.Blue;
        var deltaGreen = green - background.Green;
        var deltaRed = red - background.Red;
        var distanceSquared = deltaRed * deltaRed + deltaGreen * deltaGreen + deltaBlue * deltaBlue;
        if (distanceSquared > background.ColorDistanceSquared) return true;

        var luminance = Luminance(red, green, blue);
        var chroma = Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue));
        return luminance < background.NearWhiteLuminance && chroma > background.ChromaThreshold;
    }

    private static bool IsStrongBackground(byte[] pixels, int offset, BackgroundModel background)
    {
        var alpha = pixels[offset + 3];
        if (background.Transparent) return alpha <= background.AlphaThreshold;
        if (alpha < 224) return false;
        var blue = pixels[offset] - background.Blue;
        var green = pixels[offset + 1] - background.Green;
        var red = pixels[offset + 2] - background.Red;
        return red * red + green * green + blue * blue <= background.BackgroundSupportDistanceSquared;
    }

    private static BackgroundModel? EstimateBackground(byte[] pixels, int width, int height, int stride)
    {
        var borderWidth = Math.Max(2, (int)Math.Ceiling(Math.Min(width, height) * BorderBandRatio));
        var approximateBorderPixels = (long)borderWidth * (width + height) * 2;
        var step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((double)approximateBorderPixels / MaximumBorderSamples)));
        var red = new List<byte>();
        var green = new List<byte>();
        var blue = new List<byte>();
        var alpha = new List<byte>();
        for (var y = 0; y < height; y += step)
        {
            for (var x = 0; x < width; x += step)
            {
                if (!IsBorderSample(x, y, width, height, borderWidth)) continue;
                var offset = y * stride + x * 4;
                blue.Add(pixels[offset]);
                green.Add(pixels[offset + 1]);
                red.Add(pixels[offset + 2]);
                alpha.Add(pixels[offset + 3]);
            }
        }

        if (alpha.Count < 8) return null;
        var sortedAlpha = alpha.OrderBy(value => value).ToArray();
        var medianAlpha = Percentile(sortedAlpha, .5);
        if (medianAlpha <= 32)
        {
            var alphaThreshold = Math.Clamp((int)Percentile(sortedAlpha, .90) + 8, 8, 112);
            return new BackgroundModel(0, 0, 0, true, alphaThreshold, 0, 0, 0, 0, borderWidth);
        }

        var sortedRed = red.OrderBy(value => value).ToArray();
        var sortedGreen = green.OrderBy(value => value).ToArray();
        var sortedBlue = blue.OrderBy(value => value).ToArray();
        var backgroundRed = Percentile(sortedRed, .5);
        var backgroundGreen = Percentile(sortedGreen, .5);
        var backgroundBlue = Percentile(sortedBlue, .5);
        var backgroundLuminance = Luminance(backgroundRed, backgroundGreen, backgroundBlue);
        if (backgroundLuminance < 205) return null;

        var distances = new List<int>(red.Count);
        for (var index = 0; index < red.Count; index++)
        {
            var r = red[index] - backgroundRed;
            var g = green[index] - backgroundGreen;
            var b = blue[index] - backgroundBlue;
            distances.Add((int)Math.Ceiling(Math.Sqrt(r * r + g * g + b * b)));
        }
        distances.Sort();
        var distanceThreshold = Math.Clamp(
            Percentile(distances, BackgroundDistancePercentile) + BackgroundDistanceSafety,
            MinimumColorDistance,
            MaximumColorDistance);
        var supportThreshold = Math.Max(4, distanceThreshold - BackgroundDistanceSafety);
        var nearWhiteLuminance = Math.Clamp(backgroundLuminance - 18, 216, 244);
        var chromaThreshold = Math.Clamp(PercentileChroma(red, green, blue, .90) + 4, 10, 24);
        return new BackgroundModel(
            backgroundRed,
            backgroundGreen,
            backgroundBlue,
            false,
            0,
            distanceThreshold * distanceThreshold,
            supportThreshold * supportThreshold,
            nearWhiteLuminance,
            chromaThreshold,
            borderWidth);
    }

    private static bool IsBorderSample(int x, int y, int width, int height, int borderWidth) =>
        x < borderWidth || x >= width - borderWidth || y < borderWidth || y >= height - borderWidth;

    private static int Luminance(int red, int green, int blue) => (299 * red + 587 * green + 114 * blue) / 1000;

    private static int PercentileChroma(IReadOnlyList<byte> red, IReadOnlyList<byte> green, IReadOnlyList<byte> blue, double percentile)
    {
        var chroma = new int[red.Count];
        for (var index = 0; index < red.Count; index++)
        {
            chroma[index] = Math.Max(red[index], Math.Max(green[index], blue[index]))
                - Math.Min(red[index], Math.Min(green[index], blue[index]));
        }
        Array.Sort(chroma);
        return Percentile(chroma, percentile);
    }

    private static int RemoveIsolatedPixels(byte[] mask, int width, int height, CancellationToken cancellationToken)
    {
        var original = (byte[])mask.Clone();
        var retained = 0;
        for (var y = 0; y < height; y++)
        {
            if ((y & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var index = row + x;
                if (original[index] == 0) continue;
                var neighbours = 0;
                for (var neighbourY = Math.Max(0, y - 1); neighbourY <= Math.Min(height - 1, y + 1); neighbourY++)
                {
                    var neighbourRow = neighbourY * width;
                    for (var neighbourX = Math.Max(0, x - 1); neighbourX <= Math.Min(width - 1, x + 1); neighbourX++)
                    {
                        if (neighbourX == x && neighbourY == y) continue;
                        neighbours += original[neighbourRow + neighbourX];
                    }
                }

                if (neighbours == 0)
                {
                    mask[index] = 0;
                    continue;
                }
                retained++;
            }
        }
        return retained;
    }

    private static bool IsSignificant(Component component, int pixelThreshold)
    {
        var width = component.MaxX - component.MinX + 1;
        var height = component.MaxY - component.MinY + 1;
        return component.Count >= pixelThreshold
            || (component.Count >= 4 && (width >= MinimumDimension || height >= MinimumDimension));
    }

    private static List<Component> FindComponents(byte[] mask, int width, int height, CancellationToken cancellationToken)
    {
        var components = new List<Component>();
        var queue = ArrayPool<int>.Shared.Rent(mask.Length);
        try
        {
            for (var start = 0; start < mask.Length; start++)
            {
                if (mask[start] == 0) continue;
                var head = 0;
                var tail = 0;
                queue[tail++] = start;
                mask[start] = 0;
                var minX = start % width;
                var maxX = minX;
                var minY = start / width;
                var maxY = minY;
                var count = 0;
                while (head < tail)
                {
                    if ((count & 65_535) == 0) cancellationToken.ThrowIfCancellationRequested();
                    var current = queue[head++];
                    var x = current % width;
                    var y = current / width;
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                    count++;

                    for (var neighbourY = Math.Max(0, y - 1); neighbourY <= Math.Min(height - 1, y + 1); neighbourY++)
                    {
                        var row = neighbourY * width;
                        for (var neighbourX = Math.Max(0, x - 1); neighbourX <= Math.Min(width - 1, x + 1); neighbourX++)
                        {
                            var neighbour = row + neighbourX;
                            if (mask[neighbour] == 0) continue;
                            mask[neighbour] = 0;
                            queue[tail++] = neighbour;
                        }
                    }
                }

                if (count >= 2) components.Add(new Component(minX, minY, maxX, maxY, count));
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(queue);
        }

        return components;
    }

    private static byte Percentile(IReadOnlyList<byte> sorted, double percentile) => sorted[(int)Math.Clamp(Math.Round((sorted.Count - 1) * percentile), 0, sorted.Count - 1)];
    private static int Percentile(IReadOnlyList<int> sorted, double percentile) => sorted[(int)Math.Clamp(Math.Round((sorted.Count - 1) * percentile), 0, sorted.Count - 1)];

    private sealed record BackgroundModel(
        byte Red,
        byte Green,
        byte Blue,
        bool Transparent,
        int AlphaThreshold,
        int ColorDistanceSquared,
        int BackgroundSupportDistanceSquared,
        int NearWhiteLuminance,
        int ChromaThreshold,
        int BorderWidth);
    private sealed record Component(int MinX, int MinY, int MaxX, int MaxY, int Count);
    private sealed record ForegroundCrop(Int32Rect Bounds, Int32Rect ForegroundBounds, double ForegroundRatio, string BackgroundKind);
}

internal sealed record PdfProductImageDiagnostics(
    int SourceWidth,
    int SourceHeight,
    Int32Rect CropBounds,
    Int32Rect? ForegroundBounds,
    double ForegroundRatio,
    string BackgroundKind)
{
    internal bool AnnotationGuided { get; init; }
    internal double CropAreaRatio => (double)CropBounds.Width * CropBounds.Height / (SourceWidth * SourceHeight);
}
