namespace DocManager.Desktop;

internal static class FloatingImageLayout
{
    public static (double Width, double Height) Fit(
        double nativeWidth,
        double nativeHeight,
        double scale,
        double workAreaWidthDip,
        double workAreaHeightDip)
    {
        if (nativeWidth <= 0 || nativeHeight <= 0 || scale <= 0)
            return (nativeWidth, nativeHeight);

        if (workAreaWidthDip <= 0 || workAreaHeightDip <= 0)
            return (nativeWidth * scale, nativeHeight * scale);

        scale = Math.Max(0.1, Math.Min(8.0, scale));

        var scaledWidth = nativeWidth * scale;
        var scaledHeight = nativeHeight * scale;

        if (scaledWidth > workAreaWidthDip || scaledHeight > workAreaHeightDip)
        {
            var scaleX = workAreaWidthDip / scaledWidth;
            var scaleY = workAreaHeightDip / scaledHeight;
            var fitScale = Math.Min(scaleX, scaleY);
            scaledWidth *= fitScale;
            scaledHeight *= fitScale;
        }

        return (scaledWidth, scaledHeight);
    }
}
