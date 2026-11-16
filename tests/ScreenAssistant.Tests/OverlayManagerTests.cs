using ScreenAssistant.Core.Overlay;

namespace ScreenAssistant.Tests;

public sealed class OverlayManagerTests
{
    [Fact]
    public async Task OpensOnlyOnceAndReplaysStateAfterReopening()
    {
        var renderer = new Renderer();
        var overlay = new OverlayManager(new InlineDispatcher(), renderer);
        await overlay.SetVisibleAsync(true);
        await overlay.SetVisibleAsync(true);
        var id = await overlay.BeginAsync("Получаем ответ");
        await overlay.SetVisibleAsync(false);
        await overlay.AppendAsync(id, "7");
        await overlay.CompleteAsync(id, "7");
        Assert.Equal(OverlayPhase.Loading, renderer.State?.Phase);

        await overlay.SetVisibleAsync(true);
        Assert.Equal(new[] { true, false, true }, renderer.Visibility);
        Assert.Equal(new OverlayState("Ответ получен", "7", OverlayPhase.Ready), renderer.State);
    }

    [Fact]
    public async Task IgnoresChunksCompletionsAndErrorsFromSupersededRequests()
    {
        var renderer = new Renderer();
        var overlay = new OverlayManager(new InlineDispatcher(), renderer);
        await overlay.SetVisibleAsync(true);
        var previous = await overlay.BeginAsync("Первый");
        await overlay.AppendAsync(previous, "Старый");
        var current = await overlay.BeginAsync("Второй");
        await overlay.AppendAsync(current, "7");
        await overlay.AppendAsync(previous, "Запоздавший");
        await overlay.CompleteAsync(previous, "Неверный ответ");
        await overlay.FailAsync(previous, "Ошибка старого запроса");

        Assert.Equal(OverlayPhase.Streaming, renderer.State?.Phase);
        Assert.Equal("7", renderer.State?.Text);
        await overlay.CompleteAsync(current, "7");
        await overlay.AppendAsync(current, "Поздний фрагмент");
        Assert.Equal(OverlayPhase.Ready, renderer.State?.Phase);
        Assert.Equal("7", renderer.State?.Text);
    }

    [Fact]
    public async Task ErrorAndCancellationDiscardPartialTextButKeepLastCompleteAnswer()
    {
        var renderer = new Renderer();
        var overlay = new OverlayManager(new InlineDispatcher(), renderer);
        await overlay.SetVisibleAsync(true);
        var complete = await overlay.BeginAsync("Загрузка");
        await overlay.CompleteAsync(complete, "Первый ответ");
        var failed = await overlay.BeginAsync("Загрузка");
        await overlay.AppendAsync(failed, "Неполный");
        await overlay.FailAsync(failed, "Ошибка API");
        Assert.Equal(new OverlayState("Ошибка API", "Первый ответ", OverlayPhase.Error), renderer.State);

        var canceled = await overlay.BeginAsync("Загрузка");
        await overlay.AppendAsync(canceled, "Отменённый");
        await overlay.EndAsync("Остановлено");
        await overlay.CompleteAsync(canceled, "Запоздавший");
        Assert.Equal(new OverlayState("Остановлено", "", OverlayPhase.Idle), renderer.State);
    }

    [Fact]
    public async Task ResetClearsPreviousAnswerAndShutdownPreventsReopening()
    {
        var renderer = new Renderer();
        var overlay = new OverlayManager(new InlineDispatcher(), renderer);
        await overlay.SetVisibleAsync(true);
        var id = await overlay.BeginAsync("Загрузка");
        await overlay.CompleteAsync(id, "Прошлая тренировка");

        var newSession = await overlay.BeginAsync("Проверяем модель", reset: true);
        Assert.Equal("", renderer.State?.Text);
        await overlay.ShutdownAsync();
        await overlay.ShutdownAsync();
        await overlay.SetVisibleAsync(true);
        await overlay.AppendAsync(newSession, "Запоздавший");
        Assert.Equal(new[] { true, false }, renderer.Visibility);
        Assert.Equal("", renderer.State?.Text);
    }

    [Fact]
    public async Task BeginAfterShutdownThrows()
    {
        var overlay = new OverlayManager(new InlineDispatcher(), new Renderer());
        await overlay.ShutdownAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => overlay.BeginAsync("Поздно"));
    }

    [Fact]
    public async Task HideForRestoresVisibilityAfterDelay()
    {
        var renderer = new Renderer();
        var overlay = new OverlayManager(new InlineDispatcher(), renderer);
        await overlay.SetVisibleAsync(true);
        var hide = overlay.HideForAsync(TimeSpan.FromMilliseconds(40));
        Assert.Equal(new[] { true, false }, renderer.Visibility);
        await hide.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { true, false, true }, renderer.Visibility);
        Assert.True(renderer.ExclusionRefreshCount >= 1);
    }

    [Fact]
    public async Task HideForRepeatedCallOnlyLatestRestoreWins()
    {
        var renderer = new Renderer();
        var overlay = new OverlayManager(new InlineDispatcher(), renderer);
        await overlay.SetVisibleAsync(true);
        var first = overlay.HideForAsync(TimeSpan.FromMilliseconds(30));
        var second = overlay.HideForAsync(TimeSpan.FromMilliseconds(80));
        await first.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(renderer.Visibility[^1]);
        await second.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(renderer.Visibility[^1]);
        Assert.Equal(new[] { true, false, true }, renderer.Visibility);
    }

    [Fact]
    public async Task ClickThroughAppliesAndReappliesWhenShownAgain()
    {
        var renderer = new Renderer();
        var overlay = new OverlayManager(new InlineDispatcher(), renderer);
        await overlay.SetClickThroughAsync(false);
        Assert.Equal(new[] { false }, renderer.ClickThrough);
        await overlay.SetVisibleAsync(true);
        Assert.Equal(new[] { false }, renderer.ClickThrough);
        await overlay.SetClickThroughAsync(true);
        Assert.Equal(new[] { false, true }, renderer.ClickThrough);
        await overlay.ShutdownAsync();
        Assert.Equal(new[] { false, true, false }, renderer.ClickThrough);
    }

    [Fact]
    public async Task EnsureExcludedFalseWhenRendererReportsIncluded()
    {
        var renderer = new Renderer { Excluded = false };
        var overlay = new OverlayManager(new InlineDispatcher(), renderer);
        await overlay.SetVisibleAsync(true);
        Assert.False(await overlay.EnsureExcludedAsync());
        Assert.True(renderer.ExclusionRefreshCount >= 1);
    }

    private sealed class InlineDispatcher : IOverlayDispatcher
    {
        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class Renderer : IOverlayRenderer
    {
        public List<bool> Visibility { get; } = [];
        public List<bool> ClickThrough { get; } = [];
        public OverlayState? State { get; private set; }
        public bool Excluded { get; set; } = true;
        public int ExclusionRefreshCount { get; private set; }
        public bool IsExcludedFromCapture => Excluded;
        public void Render(OverlayState state) => State = state;
        public void SetVisible(bool visible) => Visibility.Add(visible);
        public void SetClickThrough(bool enabled) => ClickThrough.Add(enabled);
        public void RefreshCaptureExclusion() => ExclusionRefreshCount++;
    }
}
