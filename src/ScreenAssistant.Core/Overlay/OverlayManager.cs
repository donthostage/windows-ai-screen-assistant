namespace ScreenAssistant.Core.Overlay;

public sealed class OverlayManager(IOverlayDispatcher dispatcher, IOverlayRenderer renderer)
    : IOverlayService
{
    private OverlayState _state = new("Готово к тренировке", "", OverlayPhase.Idle);
    private string _lastAnswer = "";
    private long _request;
    private bool _active;
    private bool _visible;
    private bool _clickThrough = true;
    private bool _closed;
    private int _hideGeneration;
    private bool _hideShouldRestore;

    public Task SetVisibleAsync(bool visible) => dispatcher.InvokeAsync(() =>
    {
        if (_closed || visible == _visible)
            return;

        _hideShouldRestore = false;
        if (visible)
            renderer.Render(_state);

        renderer.SetVisible(visible);
        _visible = visible;
        if (visible && _clickThrough)
            renderer.SetClickThrough(true);
    });

    public Task SetClickThroughAsync(bool enabled) => dispatcher.InvokeAsync(() =>
    {
        if (_closed || enabled == _clickThrough)
            return;

        _clickThrough = enabled;
        renderer.SetClickThrough(enabled);
    });

    public async Task<bool> EnsureExcludedAsync()
    {
        var result = true;
        await dispatcher.InvokeAsync(() =>
        {
            if (_closed || !_visible)
            {
                result = true;
                return;
            }

            renderer.RefreshCaptureExclusion();
            result = renderer.IsExcludedFromCapture;
        }).ConfigureAwait(false);
        return result;
    }

    public async Task<long> BeginAsync(string status, bool reset = false)
    {
        long id = 0;
        var closed = false;
        await dispatcher.InvokeAsync(() =>
        {
            if (_closed)
            {
                closed = true;
                return;
            }

            id = ++_request;
            _active = true;
            if (reset)
                _lastAnswer = "";

            Update(new(status, _lastAnswer, OverlayPhase.Loading));
        }).ConfigureAwait(false);
        if (closed)
            throw new InvalidOperationException("Оверлей уже закрыт.");
        return id;
    }

    public Task AppendAsync(long requestId, string delta) => dispatcher.InvokeAsync(() =>
    {
        if (!IsCurrent(requestId) || delta.Length == 0)
            return;

        var prior = _state.Phase == OverlayPhase.Streaming ? _state.Text : "";
        Update(new("AI отвечает…", prior + delta, OverlayPhase.Streaming));
    });

    public Task CompleteAsync(long requestId, string text) => dispatcher.InvokeAsync(() =>
    {
        if (!IsCurrent(requestId))
            return;

        _active = false;
        _lastAnswer = text;
        Update(new("Ответ получен", text, OverlayPhase.Ready));
    });

    public Task FailAsync(long requestId, string status) => dispatcher.InvokeAsync(() =>
    {
        if (IsCurrent(requestId))
            Finish(status, error: true);
    });

    public Task EndAsync(string status, bool error = false) => dispatcher.InvokeAsync(() =>
    {
        if (_closed)
            return;

        _lastAnswer = "";
        Finish(status, error);
    });

    public async Task HideForAsync(TimeSpan delay, CancellationToken token = default)
    {
        var generation = Interlocked.Increment(ref _hideGeneration);
        await dispatcher.InvokeAsync(() =>
        {
            if (_closed)
                return;

            if (_visible)
            {
                _hideShouldRestore = true;
                renderer.SetVisible(false);
                _visible = false;
            }
        }).ConfigureAwait(false);

        try
        {
            await Task.Delay(delay, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }

        if (_closed || generation != Volatile.Read(ref _hideGeneration))
            return;

        var shouldRestore = false;
        await dispatcher.InvokeAsync(() =>
        {
            shouldRestore = !_closed && generation == Volatile.Read(ref _hideGeneration) && _hideShouldRestore;
            if (shouldRestore)
                _hideShouldRestore = false;
        }).ConfigureAwait(false);

        if (!shouldRestore)
            return;

        await SetVisibleAsync(true).ConfigureAwait(false);
        await EnsureExcludedAsync().ConfigureAwait(false);
    }

    public Task ShutdownAsync() => dispatcher.InvokeAsync(() =>
    {
        if (_closed)
            return;

        _closed = true;
        _active = false;
        _clickThrough = false;
        _hideShouldRestore = false;
        Interlocked.Increment(ref _hideGeneration);
        renderer.SetClickThrough(false);
        if (_visible)
            renderer.SetVisible(false);
        _visible = false;
    });

    private void Finish(string status, bool error)
    {
        _active = false;
        Update(new(status, _lastAnswer, error ? OverlayPhase.Error : OverlayPhase.Idle));
    }

    private bool IsCurrent(long id) => !_closed && _active && id == _request;

    private void Update(OverlayState state)
    {
        _state = state;
        if (_visible)
            renderer.Render(state);
    }
}
