using System.Runtime.InteropServices;

namespace DocManager.Desktop;

internal static class FloatingImageInterop
{
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;
    private const int GwlExstyle = -20;
    private const int SwpNosize = 0x0001;
    private const int SwpNomove = 0x0002;
    private const int SwpNoactivate = 0x0010;
    private const int LwaAlpha = 0x00000002;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotopmost = new(-2);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    public static void SetOpacityAlpha(IntPtr hwnd, byte alpha)
    {
        if (hwnd == IntPtr.Zero) return;
        EnsureLayeredWindow(hwnd);
        SetLayeredWindowAttributes(hwnd, 0, alpha, LwaAlpha);
    }

    public static void EnableClickThrough(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        EnsureLayeredWindow(hwnd);
        var current = GetWindowLongPtr(hwnd, GwlExstyle);
        var newStyle = new IntPtr(current.ToInt64() | WsExTransparent);
        SetWindowLongPtr(hwnd, GwlExstyle, newStyle);
    }

    public static void DisableClickThrough(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        EnsureLayeredWindow(hwnd);
        var current = GetWindowLongPtr(hwnd, GwlExstyle);
        var newStyle = new IntPtr(current.ToInt64() & ~WsExTransparent);
        SetWindowLongPtr(hwnd, GwlExstyle, newStyle);
    }

    private static void EnsureLayeredWindow(IntPtr hwnd)
    {
        var current = GetWindowLongPtr(hwnd, GwlExstyle);
        if ((current.ToInt64() & WsExLayered) == 0)
        {
            var newStyle = new IntPtr(current.ToInt64() | WsExLayered);
            SetWindowLongPtr(hwnd, GwlExstyle, newStyle);
        }
    }

    public static void ForceTopmost(IntPtr hwnd)
    {
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNosize | SwpNomove | SwpNoactivate);
    }

    public static void RemoveTopmost(IntPtr hwnd)
    {
        SetWindowPos(hwnd, HwndNotopmost, 0, 0, 0, 0, SwpNosize | SwpNomove | SwpNoactivate);
    }
}
