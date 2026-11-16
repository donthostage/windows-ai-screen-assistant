using ScreenAssistant.Core.AIClient;
using ScreenAssistant.Core.Licensing;

namespace ScreenAssistant.Server;

internal static class AnalysisEndpoint
{
    internal const int MaxBodyBytes = 28 * 1024 * 1024;
    private const long CreditsPerFrame = 1;

    internal static async Task<IResult> RunAsync(AnalysisRequest? request, LicenseStore store,
        Func<IReadOnlyList<ImagePayload>, bool, Task<string>> callProvider, ILogger logger, long dailyLimit)
    {
        if (request is null || request.LicenseId == Guid.Empty || request.OperationId == Guid.Empty
            || request.Signature is not { Length: 64 })
            return Results.BadRequest();

        var series = request.Frames is not null;
        IReadOnlyList<ImagePayload> frames;
        byte[] hash;
        if (request.Frames is { } submitted)
        {
            if (request.Frame is not null || request.MediaType is not null || !AnalysisFrames.IsValidSeries(submitted))
                return Results.BadRequest();

            frames = submitted;
            hash = AnalysisFrames.HashSeries(frames);
        }
        else
        {
            if (request.MediaType is null || request.Frame is null)
                return Results.BadRequest();

            var frame = new ImagePayload(request.MediaType, request.Frame);
            if (!AnalysisFrames.IsValidFrame(frame))
                return Results.BadRequest();

            frames = [frame];
            hash = DeviceActivationProof.HashAnalysisFrame(frame.MediaType, frame.Bytes);
        }

        var credits = frames.Count * CreditsPerFrame;
        if (!store.VerifyAnalysis(request.LicenseId, request.OperationId, request.Timestamp, hash, request.Signature))
            return Results.Unauthorized();

        var reservation = store.ReserveAnalysis(request.LicenseId, request.OperationId, hash, credits, dailyLimit);
        if (reservation != ReservationResult.Reserved)
        {
            return reservation switch
            {
                ReservationResult.Inactive => Results.Unauthorized(),
                ReservationResult.DailyLimit => Results.StatusCode(StatusCodes.Status429TooManyRequests),
                ReservationResult.Insufficient => Results.StatusCode(StatusCodes.Status402PaymentRequired),
                _ => Results.Conflict()
            };
        }

        string text;
        try
        {
            text = await callProvider(frames, series);
        }
        catch (Exception ex)
        {
            // A transport error does not prove that the provider did not charge.
            logger.LogWarning("Analysis {OperationId} requires reconciliation after {ErrorType}.",
                request.OperationId, ex.GetType().Name);
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }

        if (!store.SettleAnalysis(request.LicenseId, request.OperationId, credits))
            throw new InvalidOperationException("Analysis reservation could not be settled.");

        return Results.Ok(new { text });
    }
}

internal sealed record AnalysisRequest(Guid LicenseId, Guid OperationId, long Timestamp,
    byte[]? Signature, string? MediaType, byte[]? Frame, ImagePayload[]? Frames = null);
