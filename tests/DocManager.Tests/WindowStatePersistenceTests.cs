using DocManager.Core;

namespace DocManager.Tests;

public sealed class WindowStatePersistenceTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(Path.GetTempPath(), "DocManager.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Valid_state_is_restored_and_clamped_to_work_area()
    {
        var state = new WindowStateData { Left = 100, Top = 80, Width = 1400, Height = 900, WindowState = "Maximized" };
        var workAreas = new[] { new WindowBounds(0, 0, 1200, 800) };

        Assert.True(WindowPlacement.TryCreate(state, workAreas, 980, 650, out var placement));
        Assert.NotNull(placement);
        Assert.Equal(SavedWindowState.Maximized, placement.WindowState);
        Assert.Equal(new WindowBounds(100, 80, 1200, 800), placement.Bounds);
    }

    [Fact]
    public void Fixed_size_restore_uses_only_valid_position_and_forces_normal_state()
    {
        var state = new WindowStateData { Left = 100, Top = 80, Width = 1400, Height = 900, WindowState = "Maximized" };

        Assert.True(WindowPlacement.TryCreateFixedSize(state, [new WindowBounds(0, 0, 1920, 1080)], 970, 690, out var placement));
        Assert.NotNull(placement);
        Assert.Equal(SavedWindowState.Normal, placement.WindowState);
        Assert.Equal(new WindowBounds(100, 80, 970, 690), placement.Bounds);
    }

    [Fact]
    public void Fixed_size_restore_accepts_legacy_state_without_valid_size_or_state()
    {
        var state = new WindowStateData { Left = 1800, Top = 900, Width = double.NaN, Height = double.NaN, WindowState = "Unknown" };

        Assert.True(WindowPlacement.TryCreateFixedSize(state, [new WindowBounds(0, 0, 1920, 1080)], 970, 690, out var placement));
        Assert.NotNull(placement);
        Assert.Equal(new WindowBounds(1760, 900, 970, 690), placement.Bounds);
    }

    [Fact]
    public void Corrupt_json_falls_back_without_throwing()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var path = Path.Combine(_temporaryDirectory, "window-state.json");
        File.WriteAllText(path, "{ not valid json");

        Assert.False(new WindowStateStore(path).TryLoad(out var state));
        Assert.Null(state);
    }

    [Fact]
    public void Completely_off_screen_state_falls_back()
    {
        var state = new WindowStateData { Left = 3000, Top = 3000, Width = 1000, Height = 700, WindowState = "Normal" };

        Assert.False(WindowPlacement.TryCreate(state, [new WindowBounds(0, 0, 1920, 1080)], 980, 650, out var placement));
        Assert.Null(placement);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory)) Directory.Delete(_temporaryDirectory, true);
    }
}
