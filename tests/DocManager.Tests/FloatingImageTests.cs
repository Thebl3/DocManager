using DocManager.Core;
using DocManager.Desktop;

namespace DocManager.Tests;

public sealed class FloatingImageTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(Path.GetTempPath(), "DocManager.Tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(5.0, 10.0)]
    [InlineData(150.0, 100.0)]
    [InlineData(50.0, 50.0)]
    [InlineData(10.0, 10.0)]
    [InlineData(100.0, 100.0)]
    public void Opacity_clamping_by_settings(double input, double expected)
    {
        var baseDirectory = CreateDirectory("Released");
        var localAppData = CreateDirectory("LocalAppData");
        var imagePath = CreateImageFile("Images", "test.png");
        var store = new UserSettingsStore(baseDirectory, localAppData);

        store.Save(new UserSettingsData { LastFloatingImagePath = imagePath, FloatingImageOpacityPercent = input });
        var restored = store.LoadResolved();
        Assert.Equal(expected, restored.FloatingImageOpacityPercent);
    }

    [Theory]
    [InlineData(1000.0, 1000.0, 1.0, 800.0, 600.0)]
    [InlineData(500.0, 500.0, 1.0, 800.0, 600.0)]
    [InlineData(8000.0, 6000.0, 1.0, 800.0, 600.0)]
    [InlineData(1000.0, 1000.0, 2.0, 800.0, 600.0)]
    [InlineData(1000.0, 1000.0, 0.1, 800.0, 600.0)]
    [InlineData(1000.0, 1000.0, 8.0, 800.0, 600.0)]
    public void FloatingImageLayout_fit_returns_sensible_dimensions(
        double nativeWidth, double nativeHeight, double scale,
        double workAreaWidth, double workAreaHeight)
    {
        var (width, height) = FloatingImageLayout.Fit(
            nativeWidth, nativeHeight, scale,
            workAreaWidth, workAreaHeight);

        Assert.True(width > 0 && width <= workAreaWidth, $"Width {width} not in range");
        Assert.True(height > 0 && height <= workAreaHeight, $"Height {height} not in range");
    }

    [Theory]
    [InlineData(0.0, "zero work area")]
    [InlineData(-100.0, "negative work area")]
    public void FloatingImageLayout_fit_guards_negative_work_area(double workArea, string scenario)
    {
        var (width, height) = FloatingImageLayout.Fit(
            1000.0, 1000.0, 1.0,
            workArea > 0 ? workArea : 0.1, workArea > 0 ? workArea : 0.1);

        Assert.True(width > 0 && height > 0, $"Should return positive dimensions for {scenario}");
    }

    [Theory]
    [InlineData(0.0, "zero scale")]
    [InlineData(-1.0, "negative scale")]
    public void FloatingImageLayout_fit_guards_invalid_scale(double scale, string scenario)
    {
        var (width, height) = FloatingImageLayout.Fit(
            1000.0, 1000.0, scale,
            800.0, 600.0);

        Assert.True(!double.IsNaN(width) && !double.IsInfinity(width), $"Should return finite width for {scenario}");
        Assert.True(!double.IsNaN(height) && !double.IsInfinity(height), $"Should return finite height for {scenario}");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Floating_image_settings_rejects_nan_and_infinity_in_bounds(double badValue)
    {
        var baseDirectory = CreateDirectory("Released");
        var localAppData = CreateDirectory("LocalAppData");
        var imagePath = CreateImageFile("Images", "test.png");
        var store = new UserSettingsStore(baseDirectory, localAppData);

        store.Save(new UserSettingsData
        {
            LastFloatingImagePath = imagePath,
            FloatingImageLeft = badValue,
            FloatingImageTop = 100.0,
            FloatingImageWidth = 640.0,
            FloatingImageHeight = 480.0
        });

        var restored = store.LoadResolved();
        Assert.Null(restored.FloatingImageLeft);
        Assert.Null(restored.FloatingImageTop);
        Assert.Null(restored.FloatingImageWidth);
        Assert.Null(restored.FloatingImageHeight);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Floating_image_settings_rejects_invalid_bounds_at_any_coordinate(double badValue)
    {
        var baseDirectory = CreateDirectory("Released");
        var localAppData = CreateDirectory("LocalAppData");
        var imagePath = CreateImageFile("Images", "test.png");
        var store = new UserSettingsStore(baseDirectory, localAppData);

        var testCases = new[]
        {
            (Left: badValue, Top: 100.0, Width: 640.0, Height: 480.0),
            (Left: 100.0, Top: badValue, Width: 640.0, Height: 480.0),
            (Left: 100.0, Top: 100.0, Width: badValue, Height: 480.0),
            (Left: 100.0, Top: 100.0, Width: 640.0, Height: badValue),
        };

        foreach (var (left, top, width, height) in testCases)
        {
            store.Save(new UserSettingsData
            {
                LastFloatingImagePath = imagePath,
                FloatingImageLeft = left,
                FloatingImageTop = top,
                FloatingImageWidth = width,
                FloatingImageHeight = height
            });

            var restored = store.LoadResolved();
            Assert.Null(restored.FloatingImageLeft);
            Assert.Null(restored.FloatingImageTop);
            Assert.Null(restored.FloatingImageWidth);
            Assert.Null(restored.FloatingImageHeight);
        }
    }

    [Fact]
    public void Floating_image_bounds_roundtrip_and_persist()
    {
        var baseDirectory = CreateDirectory("Released");
        var localAppData = CreateDirectory("LocalAppData");
        var imagePath = CreateImageFile("Images", "test.png");
        var store = new UserSettingsStore(baseDirectory, localAppData);

        store.Save(new UserSettingsData
        {
            LastFloatingImagePath = imagePath,
            FloatingImageOpacityPercent = 75.0,
            FloatingImageTopmost = false,
            FloatingImageClickThrough = true,
            FloatingImageLeft = 100.0,
            FloatingImageTop = 200.0,
            FloatingImageWidth = 640.0,
            FloatingImageHeight = 480.0
        });

        var restored = store.LoadResolved();
        Assert.Equal(100.0, restored.FloatingImageLeft);
        Assert.Equal(200.0, restored.FloatingImageTop);
        Assert.Equal(640.0, restored.FloatingImageWidth);
        Assert.Equal(480.0, restored.FloatingImageHeight);
    }

    [Fact]
    public void Floating_image_settings_roundtrip_with_all_fields()
    {
        var baseDirectory = CreateDirectory("Released");
        var localAppData = CreateDirectory("LocalAppData");
        var imagePath = CreateImageFile("Images", "test.png");
        var store = new UserSettingsStore(baseDirectory, localAppData);

        store.Save(new UserSettingsData
        {
            LastFloatingImagePath = imagePath,
            FloatingImageOpacityPercent = 75.0,
            FloatingImageTopmost = false,
            FloatingImageClickThrough = true,
            FloatingImageLeft = 100.0,
            FloatingImageTop = 200.0,
            FloatingImageWidth = 640.0,
            FloatingImageHeight = 480.0
        });

        var restored = store.LoadResolved();
        Assert.Equal(Path.GetFullPath(imagePath), restored.LastFloatingImagePath);
        Assert.Equal(75.0, restored.FloatingImageOpacityPercent);
        Assert.False(restored.FloatingImageTopmost);
        Assert.True(restored.FloatingImageClickThrough);
        Assert.Equal(100.0, restored.FloatingImageLeft);
        Assert.Equal(200.0, restored.FloatingImageTop);
        Assert.Equal(640.0, restored.FloatingImageWidth);
        Assert.Equal(480.0, restored.FloatingImageHeight);
    }

    [Fact]
    public void Floating_image_path_clamps_opacity_on_save()
    {
        var baseDirectory = CreateDirectory("Released");
        var localAppData = CreateDirectory("LocalAppData");
        var imagePath = CreateImageFile("Images", "test.png");
        var store = new UserSettingsStore(baseDirectory, localAppData);

        store.Save(new UserSettingsData
        {
            LastFloatingImagePath = imagePath,
            FloatingImageOpacityPercent = 150.0
        });

        var restored = store.LoadResolved();
        Assert.Equal(100.0, restored.FloatingImageOpacityPercent);
    }

    [Fact]
    public void Floating_image_opacity_clamps_below_10_on_save()
    {
        var baseDirectory = CreateDirectory("Released");
        var localAppData = CreateDirectory("LocalAppData");
        var imagePath = CreateImageFile("Images", "test.png");
        var store = new UserSettingsStore(baseDirectory, localAppData);

        store.Save(new UserSettingsData
        {
            LastFloatingImagePath = imagePath,
            FloatingImageOpacityPercent = 5.0
        });

        var restored = store.LoadResolved();
        Assert.Equal(10.0, restored.FloatingImageOpacityPercent);
    }

    [Fact]
    public void Missing_floating_image_file_is_cleared_on_load()
    {
        var baseDirectory = CreateDirectory("Released");
        var localAppData = CreateDirectory("LocalAppData");
        var imagePath = CreateImageFile("Images", "test.png");
        var store = new UserSettingsStore(baseDirectory, localAppData);

        store.Save(new UserSettingsData { LastFloatingImagePath = imagePath });
        File.Delete(imagePath);

        var restored = store.LoadResolved();
        Assert.Null(restored.LastFloatingImagePath);
    }

    [Fact]
    public void Floating_image_defaults_are_sensible()
    {
        var baseDirectory = CreateDirectory("Released");
        var localAppData = CreateDirectory("LocalAppData");
        var store = new UserSettingsStore(baseDirectory, localAppData);

        var restored = store.LoadResolved();
        Assert.Null(restored.LastFloatingImagePath);
        Assert.Equal(100.0, restored.FloatingImageOpacityPercent);
        Assert.True(restored.FloatingImageTopmost);
        Assert.False(restored.FloatingImageClickThrough);
        Assert.Null(restored.FloatingImageLeft);
        Assert.Null(restored.FloatingImageTop);
        Assert.Null(restored.FloatingImageWidth);
        Assert.Null(restored.FloatingImageHeight);
    }

    [Fact]
    public void Floating_image_supports_multiple_image_formats()
    {
        var baseDirectory = CreateDirectory("Released");
        var localAppData = CreateDirectory("LocalAppData");
        var store = new UserSettingsStore(baseDirectory, localAppData);

        var formats = new[] { "png", "jpg", "jpeg", "bmp", "gif", "tif", "tiff", "webp" };
        foreach (var format in formats)
        {
            var imagePath = CreateImageFile("Images", $"test.{format}");
            store.Save(new UserSettingsData { LastFloatingImagePath = imagePath });
            var restored = store.LoadResolved();
            Assert.Equal(Path.GetFullPath(imagePath), restored.LastFloatingImagePath);
            File.Delete(imagePath);
        }
    }

    private string CreateDirectory(params string[] parts)
    {
        var path = parts.Aggregate(_temporaryDirectory, Path.Combine);
        Directory.CreateDirectory(path);
        return path;
    }

    private string CreateImageFile(string directory, string fileName)
    {
        var dir = CreateDirectory(directory);
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, "fake image data");
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory)) Directory.Delete(_temporaryDirectory, true);
    }
}
