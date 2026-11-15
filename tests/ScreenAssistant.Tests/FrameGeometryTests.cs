using ScreenAssistant.Core.Geometry;

namespace ScreenAssistant.Tests;

public sealed class FrameGeometryTests
{
    [Fact]
    public void ClampsNegativeCoordinates()
    {
        var result = FrameGeometry.NormalizeCrop(new CropRect(-20, -5, 100, 80), 200, 150);
        Assert.Equal(new CropRect(0, 0, 80, 75), result);
    }

    [Fact]
    public void ClampsOverflowPastFrameBounds()
    {
        var result = FrameGeometry.NormalizeCrop(new CropRect(150, 100, 100, 100), 200, 150);
        Assert.Equal(new CropRect(150, 100, 50, 50), result);
    }

    [Fact]
    public void RejectsZeroArea()
    {
        Assert.Equal(default, FrameGeometry.NormalizeCrop(new CropRect(10, 10, 0, 40), 100, 100));
        Assert.Equal(default, FrameGeometry.NormalizeCrop(new CropRect(10, 10, 40, 0), 100, 100));
        Assert.Equal(default, FrameGeometry.NormalizeCrop(new CropRect(200, 10, 40, 40), 100, 100));
        Assert.Equal(default, FrameGeometry.NormalizeCrop(new CropRect(-60, 10, 40, 40), 100, 100));
        Assert.True(default(CropRect).IsEmpty);
    }

    [Fact]
    public void TryReadStoredRequiresCompletePositiveSize()
    {
        Assert.Null(FrameGeometry.TryReadStored(null, 0, 10, 10));
        Assert.Null(FrameGeometry.TryReadStored(0, 0, 0, 10));
        Assert.Equal(new CropRect(8, 12, 40, 30), FrameGeometry.TryReadStored(8, 12, 40, 30));
    }

    [Fact]
    public void NormalizeUsesRealFrameNotInventedBounds()
    {
        // Stored crop larger than a later smaller frame must clamp to that frame.
        var stored = FrameGeometry.TryReadStored(10, 10, 800, 600)!.Value;
        var onSmallFrame = FrameGeometry.NormalizeCrop(stored, 100, 80);
        Assert.Equal(new CropRect(10, 10, 90, 70), onSmallFrame);
    }
}
