namespace ScreenAssistant.Core.Overlay;

public enum OverlayPhase { Idle, Loading, Streaming, Ready, Error }

public sealed record OverlayState(string Status, string Text, OverlayPhase Phase);

public interface IOverlayService
{
    Task SetVisibleAsync(bool visible);
    Task SetClickThroughAsync(bool enabled);
    Task<bool> EnsureExcludedAsync();
    Task<long> BeginAsync(string status, bool reset = false);
    Task AppendAsync(long requestId, string delta);
    Task CompleteAsync(long requestId, string text);
    Task FailAsync(long requestId, string status);
    Task EndAsync(string status, bool error = false);
    Task HideForAsync(TimeSpan delay, CancellationToken token = default);
    Task ShutdownAsync();
}

public interface IOverlayRenderer
{
    void Render(OverlayState state);
    void SetVisible(bool visible);
    void SetClickThrough(bool enabled);
    void RefreshCaptureExclusion();
    bool IsExcludedFromCapture { get; }
}

public interface IOverlayDispatcher
{
    Task InvokeAsync(Action action);
}
