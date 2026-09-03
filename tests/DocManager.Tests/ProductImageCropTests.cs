using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DocManager.Dialux;
using UglyToad.PdfPig.Annotations;
using UglyToad.PdfPig.Core;

namespace DocManager.Tests;

public sealed class ProductImageCropTests
{
    [Theory]
    [MemberData(nameof(OpaqueCases))]
    public void Adaptive_crop_retains_all_foreground_with_padding_and_reduces_whitespace(Fixture fixture)
    {
        var result = Assert.IsType<PdfProductImage>(AdaptiveImageCropper.CropAndLimit(fixture.Png, fixture.Width, fixture.Height, CancellationToken.None));
        var diagnostics = Assert.IsType<PdfProductImageDiagnostics>(result.Diagnostics);

        Assert.True(diagnostics.CropAreaRatio < fixture.MaximumCropRatio, $"Crop ratio {diagnostics.CropAreaRatio:0.000}");
        AssertContainsWithPadding(diagnostics.CropBounds, fixture.ForegroundBounds, fixture.MinimumPadding);
        Assert.True(result.Png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
    }

    public static TheoryData<Fixture> OpaqueCases => new()
    {
        Fixture.Opaque("small centered", 500, 360, [new Int32Rect(205, 145, 90, 70)], new Int32Rect(205, 145, 90, 70), .12),
        Fixture.Opaque("edge wide", 800, 260, [new Int32Rect(28, 96, 744, 68)], new Int32Rect(28, 96, 744, 68), .45),
        Fixture.Opaque("portrait", 280, 620, [new Int32Rect(105, 70, 70, 480)], new Int32Rect(105, 70, 70, 480), .42),
        Fixture.Opaque("multiple parts", 640, 360, [new Int32Rect(105, 125, 100, 80), new Int32Rect(430, 145, 85, 65)], new Int32Rect(105, 125, 410, 85), .22),
        Fixture.Opaque("light gray", 520, 360, [new Int32Rect(155, 100, 210, 160)], new Int32Rect(155, 100, 210, 160), .30, product: Color.FromRgb(230, 231, 232)),
        Fixture.Opaque("jpeg noise", 560, 360, [new Int32Rect(175, 115, 210, 130)], new Int32Rect(175, 115, 210, 130), .25, noise: true)
    };

    [Fact]
    public void Adaptive_crop_preserves_transparent_background_and_product()
    {
        var fixture = Fixture.Transparent(440, 360, new Int32Rect(145, 90, 150, 180));
        var result = Assert.IsType<PdfProductImage>(AdaptiveImageCropper.CropAndLimit(fixture.Png, fixture.Width, fixture.Height, CancellationToken.None));
        var diagnostics = Assert.IsType<PdfProductImageDiagnostics>(result.Diagnostics);

        Assert.Equal("transparent", diagnostics.BackgroundKind);
        Assert.True(diagnostics.CropAreaRatio < .35);
        AssertContainsWithPadding(diagnostics.CropBounds, fixture.ForegroundBounds, 6);
        using var stream = new MemoryStream(result.Png, writable: false);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        converted.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
        Assert.Equal(0, pixel[3]);
    }

    [Fact]
    public void Uniform_uncertain_image_falls_back_to_uncropped_source()
    {
        var png = Encode(360, 240, (_, _) => Color.FromRgb(248, 248, 248));
        var result = Assert.IsType<PdfProductImage>(AdaptiveImageCropper.CropAndLimit(png, 360, 240, CancellationToken.None));
        var diagnostics = Assert.IsType<PdfProductImageDiagnostics>(result.Diagnostics);
        Assert.Equal(new Int32Rect(0, 0, 360, 240), diagnostics.CropBounds);
    }

    [Fact]
    public void Ambiguous_gradient_background_falls_back_to_uncropped_source()
    {
        var png = Encode(420, 280, (x, _) =>
        {
            var value = (byte)(220 + x * 30 / 419);
            return Color.FromRgb(value, value, value);
        });
        var result = Assert.IsType<PdfProductImage>(AdaptiveImageCropper.CropAndLimit(png, 420, 280, CancellationToken.None));
        var diagnostics = Assert.IsType<PdfProductImageDiagnostics>(result.Diagnostics);
        Assert.Equal(new Int32Rect(0, 0, 420, 280), diagnostics.CropBounds);
    }

    [Fact]
    public void Thin_meaningful_product_edges_survive_noise_cleanup()
    {
        var foreground = new Int32Rect(185, 80, 130, 210);
        var png = Encode(500, 360, (x, y) =>
        {
            var onOutline = (x == foreground.X || x == foreground.X + foreground.Width - 1)
                && y >= foreground.Y && y < foreground.Y + foreground.Height
                || (y == foreground.Y || y == foreground.Y + foreground.Height - 1)
                && x >= foreground.X && x < foreground.X + foreground.Width;
            return onOutline ? Color.FromRgb(210, 212, 214) : Color.FromRgb(249, 249, 249);
        });
        var result = Assert.IsType<PdfProductImage>(AdaptiveImageCropper.CropAndLimit(png, 500, 360, CancellationToken.None));
        var diagnostics = Assert.IsType<PdfProductImageDiagnostics>(result.Diagnostics);
        AssertContainsWithPadding(diagnostics.CropBounds, foreground, 5);
    }

    [Fact]
    public void Annotation_selector_prefers_semantic_reference_text_and_nearest_square()
    {
        var annotations = new[]
        {
            (AnnotationType.Square, (string?)null, new PdfRectangle(20, 20, 80, 80), true),
            (AnnotationType.FreeText, "  REFERENCE\r\n image  ", new PdfRectangle(315, 355, 410, 375), false),
            (AnnotationType.Square, (string?)null, new PdfRectangle(290, 180, 520, 350), true)
        };
        Assert.Equal(new PdfRectangle(290, 180, 520, 350), PdfPigProductImageExtractor.SelectReferenceImageRegion(annotations));
    }

    [Fact]
    public void Annotation_selector_requires_free_text_reference_and_red_square()
    {
        var reference = new PdfRectangle(100, 200, 220, 230);
        var square = new PdfRectangle(90, 80, 260, 190);
        Assert.Null(PdfPigProductImageExtractor.SelectReferenceImageRegion([(AnnotationType.FreeText, "Reference image", reference, false)]));
        Assert.Null(PdfPigProductImageExtractor.SelectReferenceImageRegion([(AnnotationType.Text, "Reference image", reference, false), (AnnotationType.Square, (string?)null, square, true)]));
        Assert.Null(PdfPigProductImageExtractor.SelectReferenceImageRegion([(AnnotationType.FreeText, "Reference image", reference, false), (AnnotationType.Square, (string?)null, square, false)]));
        Assert.Null(PdfPigProductImageExtractor.SelectReferenceImageRegion([(AnnotationType.FreeText, "technical drawing", reference, false), (AnnotationType.Square, (string?)null, square, true)]));
    }

    [Fact]
    public void Annotation_guided_selector_prefers_close_fitting_image_over_much_larger_containing_composite()
    {
        var selector = new PdfRectangle(100, 100, 300, 250);
        var intendedImage = new PdfRectangle(96, 96, 304, 254);
        var containingComposite = new PdfRectangle(0, 0, 600, 800);

        var selectedIndex = PdfPigProductImageExtractor.SelectAnnotationGuidedCandidateIndex([containingComposite, intendedImage], selector);

        Assert.Equal(1, selectedIndex);
    }

    [Theory]
    [InlineData(@"D:\Claude\DocManager\Product Family\Smartbright Pro DN068B G2\911401862587 - DN068B G2 LED13_840 PSU D125.pdf", .12, .32, 1.70, 2.05, 328, 240, 465, 273)]
    [InlineData(@"D:\Claude\DocManager\Product Family\GreenPerform HighBay Elite G2\911401628209 - BY778P LED350_NW PSD WB.pdf", .55, .92, 1.65, 1.95, 56, 72, 1011, 569)]
    public void Optional_actual_examples_are_adaptively_cropped_without_fixed_inset_clipping(
        string path,
        double minimumCropArea,
        double maximumCropArea,
        double minimumAspect,
        double maximumAspect,
        int foregroundX,
        int foregroundY,
        int foregroundWidth,
        int foregroundHeight)
    {
        if (!File.Exists(path)) return;
        var image = Assert.IsType<PdfProductImage>(new PdfPigProductImageExtractor().Extract(path));
        var diagnostics = Assert.IsType<PdfProductImageDiagnostics>(image.Diagnostics);

        Assert.Equal(1125, diagnostics.SourceWidth);
        Assert.Equal(750, diagnostics.SourceHeight);
        Assert.InRange(diagnostics.CropAreaRatio, minimumCropArea, maximumCropArea);
        Assert.InRange(image.Aspect, minimumAspect, maximumAspect);
        Assert.NotNull(diagnostics.ForegroundBounds);
        Assert.False(diagnostics.AnnotationGuided);
        AssertContainsWithPadding(diagnostics.CropBounds, diagnostics.ForegroundBounds!.Value, 1);
        AssertContainsWithPadding(
            diagnostics.CropBounds,
            new Int32Rect(foregroundX, foregroundY, foregroundWidth, foregroundHeight),
            1);
    }

    [Fact]
    public void Source_dimensions_must_match_png_header()
    {
        var png = Encode(360, 240, (_, _) => Color.FromRgb(248, 248, 248));
        Assert.Null(AdaptiveImageCropper.CropAndLimit(png, 361, 240, CancellationToken.None));
    }

    private static void AssertContainsWithPadding(Int32Rect crop, Int32Rect foreground, int minimumPadding)
    {
        Assert.True(crop.X <= foreground.X - minimumPadding, $"left crop={crop}, foreground={foreground}");
        Assert.True(crop.Y <= foreground.Y - minimumPadding, $"top crop={crop}, foreground={foreground}");
        Assert.True(crop.X + crop.Width >= foreground.X + foreground.Width + minimumPadding, $"right crop={crop}, foreground={foreground}");
        Assert.True(crop.Y + crop.Height >= foreground.Y + foreground.Height + minimumPadding, $"bottom crop={crop}, foreground={foreground}");
    }

    private static bool Contains(Int32Rect rectangle, int x, int y) =>
        x >= rectangle.X && x < rectangle.X + rectangle.Width && y >= rectangle.Y && y < rectangle.Y + rectangle.Height;

    public sealed record Fixture(string Name, int Width, int Height, byte[] Png, Int32Rect ForegroundBounds, double MaximumCropRatio, int MinimumPadding = 5)
    {
        public override string ToString() => Name;

        public static Fixture Opaque(string name, int width, int height, IReadOnlyList<Int32Rect> components, Int32Rect foreground, double maximumCropRatio, Color? product = null, bool noise = false)
        {
            var productColor = product ?? Color.FromRgb(70, 90, 110);
            var random = new Random(1776);
            var png = Encode(width, height, (x, y) =>
            {
                if (components.Any(rectangle => Contains(rectangle, x, y))) return productColor;
                var variation = noise ? random.Next(-5, 6) : 0;
                var background = (byte)Math.Clamp(247 + variation, 0, 255);
                return Color.FromRgb(background, background, background);
            });
            return new Fixture(name, width, height, png, foreground, maximumCropRatio);
        }

        public static Fixture Transparent(int width, int height, Int32Rect foreground)
        {
            var png = Encode(width, height, (x, y) => Contains(foreground, x, y) ? Color.FromArgb(220, 80, 100, 130) : Colors.Transparent);
            return new Fixture("transparent", width, height, png, foreground, .35);
        }
    }

    private static byte[] Encode(int width, int height, Func<int, int, Color> colorAt)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var color = colorAt(x, y);
                var offset = y * stride + x * 4;
                pixels[offset] = color.B;
                pixels[offset + 1] = color.G;
                pixels[offset + 2] = color.R;
                pixels[offset + 3] = color.A;
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}
