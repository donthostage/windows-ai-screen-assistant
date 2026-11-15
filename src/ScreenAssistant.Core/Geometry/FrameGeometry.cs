namespace ScreenAssistant.Core.Geometry;

public readonly record struct CropRect(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

public static class FrameGeometry
{
    /// <summary>
    /// Clamps a crop to the frame. Empty / non-positive crops become default.
    /// Callers must pass the real bitmap size — do not invent dimensions.
    /// </summary>
    public static CropRect NormalizeCrop(CropRect crop, int width, int height)
    {
        if (width <= 0 || height <= 0 || crop.Width <= 0 || crop.Height <= 0)
            return default;

        var x = Math.Clamp(crop.X, 0, width);
        var y = Math.Clamp(crop.Y, 0, height);
        var w = Math.Min((long)crop.X + crop.Width, width) - x;
        var h = Math.Min((long)crop.Y + crop.Height, height) - y;
        if (w <= 0 || h <= 0)
            return default;

        return new(x, y, (int)w, (int)h);
    }

    /// <summary>
    /// Reads a persisted crop without clamping to a frame. Returns null if incomplete.
    /// </summary>
    public static CropRect? TryReadStored(int? x, int? y, int? width, int? height)
    {
        if (x is null || y is null || width is not > 0 || height is not > 0)
            return null;
        return new(x.Value, y.Value, width.Value, height.Value);
    }
}
