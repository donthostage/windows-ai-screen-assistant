using System.Security.Cryptography;
using ScreenAssistant.Core.AIClient;

namespace ScreenAssistant.Core.Licensing;

public static class AnalysisFrames
{
    public const int MaximumCount = 8;
    public const int MaximumFrameBytes = 8 * 1024 * 1024;
    public const int MaximumSeriesBytes = 20 * 1024 * 1024;

    public static bool IsValidFrame(ImagePayload? frame) =>
        frame?.MediaType is ("image/png" or "image/jpeg")
        && frame.Bytes is { Length: > 0 and <= MaximumFrameBytes };

    public static bool IsValidSeries(IReadOnlyList<ImagePayload> frames) =>
        frames.Count is > 0 and <= MaximumCount
        && frames.All(IsValidFrame)
        && frames.Sum(frame => frame.Bytes.Length) <= MaximumSeriesBytes;

    public static byte[] HashSeries(IReadOnlyList<ImagePayload> frames)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("ScreenAssistant/analysis-series/v1\0"u8);
        foreach (var frame in frames)
            hash.AppendData(DeviceActivationProof.HashAnalysisFrame(frame.MediaType, frame.Bytes));

        return hash.GetHashAndReset();
    }
}
