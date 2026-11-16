using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenAssistant.Configuration;
using ScreenAssistant.Core.AIClient;
using ScreenAssistant.Core.Geometry;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using BitmapFrame = System.Windows.Media.Imaging.BitmapFrame;
using WinRtBitmapEncoder = Windows.Graphics.Imaging.BitmapEncoder;

namespace ScreenAssistant.ScreenCapture;

internal sealed record CapturedImage(ImagePayload Payload, string Fingerprint, int Width, int Height);

internal static class FrameEncoding
{
    private const int LongEdgeCropped = 2560;
    private const int LongEdgeFull = 3000;
    private const int PngLimit = 8 * 1024 * 1024;

    public static Task<CapturedImage> EncodeAsync(SoftwareBitmap bitmap, CancellationToken token) =>
        EncodeAsync(bitmap, null, token);

    public static async Task<CapturedImage> EncodeAsync(SoftwareBitmap bitmap, CropRect? crop,
        CancellationToken token)
    {
        CropRect bounds;
        var longEdge = LongEdgeFull;
        if (crop is { } requested && !requested.IsEmpty)
        {
            var normalized = FrameGeometry.NormalizeOrNull(requested, bitmap.PixelWidth, bitmap.PixelHeight);
            if (normalized is null)
            {
                LocalFiles.Log("frame-crop",
                    new CaptureException("Выбранная область находится за пределами кадра. Выберите область заново."));
                bounds = new CropRect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
            }
            else
            {
                bounds = normalized.Value;
                longEdge = LongEdgeCropped;
            }
        }
        else
        {
            bounds = new CropRect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
        }

        if (bounds.IsEmpty)
            bounds = new CropRect(0, 0, Math.Max(1, bitmap.PixelWidth), Math.Max(1, bitmap.PixelHeight));

        var scale = Math.Min(1.0, longEdge / (double)Math.Max(bounds.Width, bounds.Height));
        var width = Math.Max(1, (int)Math.Round(bounds.Width * scale));
        var height = Math.Max(1, (int)Math.Round(bounds.Height * scale));

        using var stream = new InMemoryRandomAccessStream();
        var encoder = await WinRtBitmapEncoder.CreateAsync(WinRtBitmapEncoder.PngEncoderId, stream).AsTask(token);
        encoder.SetSoftwareBitmap(bitmap);
        encoder.BitmapTransform.Bounds = new BitmapBounds
        {
            X = (uint)bounds.X,
            Y = (uint)bounds.Y,
            Width = (uint)bounds.Width,
            Height = (uint)bounds.Height
        };
        if (scale < 1)
        {
            encoder.BitmapTransform.ScaledWidth = (uint)width;
            encoder.BitmapTransform.ScaledHeight = (uint)height;
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        }
        await encoder.FlushAsync().AsTask(token);
        stream.Seek(0);
        using var source = stream.AsStreamForRead();
        using var memory = new MemoryStream();
        await source.CopyToAsync(memory, token);
        var png = memory.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(png));
        var payload = png.Length <= PngLimit
            ? new ImagePayload("image/png", png)
            : ToJpeg(png);
        token.ThrowIfCancellationRequested();
        return new CapturedImage(payload, hash, width, height);
    }

    public static BitmapSource Decode(ImagePayload payload)
    {
        using var input = new MemoryStream(payload.Bytes, writable: false);
        var bitmap = BitmapFrame.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        bitmap.Freeze();
        return bitmap;
    }

    public static (ImagePayload Image, string ExpectedColors) CreateVisionProbe()
    {
        var palette = new[] { ("RED", 2), ("GREEN", 1), ("BLUE", 0) };
        var left = RandomNumberGenerator.GetInt32(palette.Length);
        var middle = (left + RandomNumberGenerator.GetInt32(1, 3)) % palette.Length;
        var right = (middle + RandomNumberGenerator.GetInt32(1, 3)) % palette.Length;
        var expected = $"{palette[left].Item1},{palette[middle].Item1},{palette[right].Item1}";

        var pixels = new byte[48];
        WriteBand(pixels, 0, palette[left].Item2);
        WriteBand(pixels, 1, palette[middle].Item2);
        WriteBand(pixels, 2, palette[right].Item2);
        var source = BitmapSource.Create(12, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 48);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = new MemoryStream();
        encoder.Save(output);
        return (new ImagePayload("image/png", output.ToArray()), expected);

        static void WriteBand(byte[] buffer, int band, int channel)
        {
            for (var x = band * 4; x < band * 4 + 4; x++)
            {
                var i = x * 4;
                buffer[i + channel] = 255;
                buffer[i + 3] = 255;
            }
        }
    }

    private static ImagePayload ToJpeg(byte[] png)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = 95 };
        encoder.Frames.Add(BitmapFrame.Create(Decode(new ImagePayload("image/png", png))));
        using var output = new MemoryStream();
        encoder.Save(output);
        return new ImagePayload("image/jpeg", output.ToArray());
    }
}
