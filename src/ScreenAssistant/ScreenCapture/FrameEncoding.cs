using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    private const int LongEdge = 2560;
    private const int PngLimit = 8 * 1024 * 1024;

    public static Task<CapturedImage> EncodeAsync(SoftwareBitmap bitmap, CancellationToken token) =>
        EncodeAsync(bitmap, null, token);

    public static async Task<CapturedImage> EncodeAsync(SoftwareBitmap bitmap, CropRect? crop,
        CancellationToken token)
    {
        var bounds = crop is { } requested && !requested.IsEmpty
            ? FrameGeometry.NormalizeCrop(requested, bitmap.PixelWidth, bitmap.PixelHeight)
            : new CropRect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
        if (bounds.IsEmpty)
            throw new CaptureException("Выбранная область находится за пределами кадра. Выберите область заново.");

        var scale = Math.Min(1.0, LongEdge / (double)Math.Max(bounds.Width, bounds.Height));
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
        var bands = new[] { palette[left], palette[middle], palette[right] };

        const int bandWidth = 64;
        const int width = bandWidth * 3;
        const int height = 96;
        const int stride = width * 3;
        var pixels = new byte[height * stride];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var (_, channel) = bands[x / bandWidth];
            pixels[y * stride + x * 3 + channel] = 255;
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, pixels, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        var colors = string.Join(" ", bands.Select(band => band.Item1));
        return (new ImagePayload("image/png", output.ToArray()), colors);
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
