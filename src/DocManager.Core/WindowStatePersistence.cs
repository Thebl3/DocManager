using System.Text.Json;

namespace DocManager.Core;

public sealed record WindowBounds(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

public sealed record WindowStateData
{
    public double Left { get; init; }
    public double Top { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public string WindowState { get; init; } = "Normal";
}

public enum SavedWindowState
{
    Normal,
    Maximized
}

public sealed record WindowPlacement(WindowBounds Bounds, SavedWindowState WindowState)
{
    private const double MinimumVisibleWidth = 160;
    private const double MinimumVisibleHeight = 32;

    public static bool TryCreate(
        WindowStateData? state,
        IReadOnlyList<WindowBounds> workAreas,
        double minimumWidth,
        double minimumHeight,
        out WindowPlacement? placement)
    {
        placement = null;
        if (state is null || workAreas.Count == 0 || !IsFinitePositive(state.Width) || !IsFinitePositive(state.Height) ||
            !IsFinite(state.Left) || !IsFinite(state.Top) || !IsFinitePositive(minimumWidth) || !IsFinitePositive(minimumHeight) ||
            !TryGetWindowState(state.WindowState, out var windowState))
            return false;

        var savedBounds = new WindowBounds(state.Left, state.Top, state.Width, state.Height);
        var workArea = workAreas
            .Where(IsUsableWorkArea)
            .Select(area => new { Area = area, Overlap = OverlapArea(savedBounds, area) })
            .OrderByDescending(item => item.Overlap)
            .FirstOrDefault();
        if (workArea is null || workArea.Overlap <= 0)
            return false;

        var area = workArea.Area;
        var width = Math.Clamp(state.Width, minimumWidth, Math.Max(minimumWidth, area.Width));
        var height = Math.Clamp(state.Height, minimumHeight, Math.Max(minimumHeight, area.Height));
        var visibleWidth = Math.Min(MinimumVisibleWidth, width);
        var visibleHeight = Math.Min(MinimumVisibleHeight, height);
        var left = Math.Clamp(state.Left, area.Left - width + visibleWidth, area.Right - visibleWidth);
        var top = Math.Clamp(state.Top, area.Top, area.Bottom - visibleHeight);

        placement = new WindowPlacement(new WindowBounds(left, top, width, height), windowState);
        return true;
    }

    public static bool TryCreateFixedSize(
        WindowStateData? state,
        IReadOnlyList<WindowBounds> workAreas,
        double width,
        double height,
        out WindowPlacement? placement)
    {
        placement = null;
        if (state is null || workAreas.Count == 0 || !IsFinite(state.Left) || !IsFinite(state.Top) ||
            !IsFinitePositive(width) || !IsFinitePositive(height))
            return false;

        var savedBounds = new WindowBounds(state.Left, state.Top, width, height);
        var workArea = workAreas
            .Where(IsUsableWorkArea)
            .Select(area => new { Area = area, Overlap = OverlapArea(savedBounds, area) })
            .OrderByDescending(item => item.Overlap)
            .FirstOrDefault();
        if (workArea is null || workArea.Overlap <= 0)
            return false;

        var area = workArea.Area;
        var visibleWidth = Math.Min(MinimumVisibleWidth, width);
        var visibleHeight = Math.Min(MinimumVisibleHeight, height);
        var left = Math.Clamp(state.Left, area.Left - width + visibleWidth, area.Right - visibleWidth);
        var top = Math.Clamp(state.Top, area.Top, area.Bottom - visibleHeight);

        placement = new WindowPlacement(new WindowBounds(left, top, width, height), SavedWindowState.Normal);
        return true;
    }

    private static bool TryGetWindowState(string? value, out SavedWindowState windowState)
    {
        if (string.Equals(value, "Maximized", StringComparison.OrdinalIgnoreCase))
        {
            windowState = SavedWindowState.Maximized;
            return true;
        }

        if (string.Equals(value, "Normal", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "Minimized", StringComparison.OrdinalIgnoreCase))
        {
            windowState = SavedWindowState.Normal;
            return true;
        }

        windowState = default;
        return false;
    }

    private static double OverlapArea(WindowBounds first, WindowBounds second)
    {
        var width = Math.Max(0, Math.Min(first.Right, second.Right) - Math.Max(first.Left, second.Left));
        var height = Math.Max(0, Math.Min(first.Bottom, second.Bottom) - Math.Max(first.Top, second.Top));
        return width * height;
    }

    private static bool IsUsableWorkArea(WindowBounds bounds) =>
        IsFinite(bounds.Left) && IsFinite(bounds.Top) && IsFinitePositive(bounds.Width) && IsFinitePositive(bounds.Height);

    private static bool IsFinitePositive(double value) => IsFinite(value) && value > 0;
    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}

public sealed class WindowStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public WindowStateStore(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DocManager",
            "window-state.json");
    }

    public string Path { get; }

    public bool TryLoad(out WindowStateData? state)
    {
        state = null;
        try
        {
            if (!File.Exists(Path)) return false;
            state = JsonSerializer.Deserialize<WindowStateData>(File.ReadAllText(Path), JsonOptions);
            return state is not null;
        }
        catch (IOException)
        {
            state = null;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            state = null;
            return false;
        }
        catch (JsonException)
        {
            state = null;
            return false;
        }
        catch (NotSupportedException)
        {
            state = null;
            return false;
        }
    }

    public void Save(WindowStateData state)
    {
        ArgumentNullException.ThrowIfNull(state);
        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
            temporaryPath = AtomicFile.CreateSiblingTemporaryPath(Path);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, JsonOptions));
            AtomicFile.Replace(temporaryPath, Path);
        }
        catch (IOException)
        {
            // Window placement is optional user state; closing the application must not fail when it cannot be saved.
        }
        catch (UnauthorizedAccessException)
        {
            // Window placement is optional user state; closing the application must not fail when it cannot be saved.
        }
        catch (NotSupportedException)
        {
            // Window placement is optional user state; closing the application must not fail when it cannot be saved.
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
