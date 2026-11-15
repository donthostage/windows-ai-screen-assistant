using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ScreenAssistant.Configuration;
using ScreenAssistant.Core.AIClient;
using ScreenAssistant.Core.Overlay;
using ScreenAssistant.Hotkeys;
using ScreenAssistant.Overlay;
using ScreenAssistant.ScreenCapture;
using ScreenAssistant.Tray;
using Windows.Graphics.Capture;

namespace ScreenAssistant;

public partial class MainWindow : Window
{
    private static readonly Brush Idle = Brushes.SlateGray;
    private static readonly Brush Active = Brushes.SeaGreen;
    private static readonly Brush Error = Brushes.Firebrick;

    private readonly AiClient _ai;
    private readonly IOverlayService _overlay;
    private readonly Action<bool> _persistOverlayEnabled;
    private readonly ObservableCollection<HistoryEntry> _history = new();
    private GraphicsCaptureItem? _source;
    private Uri? _keyEndpoint;
    private CancellationTokenSource? _operation;
    private TaskCompletionSource? _operationFinished;
    private long _requestVersion;
    private readonly DispatcherTimer _periodicTimer = new();
    private PeriodicAnalysis? _periodic;
    private string? _lastAnalyzedFrame;
    private TimeSpan _retryDelay;
    private HotkeyManager? _hotkeys;
    private AppTrayIcon? _tray;
    private HwndSource? _hwndSource;
    private bool _borderlessCapture;
    private bool _initialized;
    private bool _closing;
    private bool _exitRequested;
    private bool _shutdownComplete;
    private bool _sourceLost;
    private bool _operationUsesSource;
    private int _requests;
    private int _answers;

    internal MainWindow(AiClient ai, IOverlayService overlay,
        bool overlayEnabled, Action<bool> persistOverlayEnabled)
    {
        _ai = ai;
        _overlay = overlay;
        _persistOverlayEnabled = persistOverlayEnabled;
        InitializeComponent();
        _periodicTimer.Tick += async (_, _) => await AnalyzePeriodicFrameAsync();
        ShowInTaskbar = false;
        HistoryList.ItemsSource = _history;
        OverlayEnabled.IsChecked = overlayEnabled;
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
                HideToTray();
        };
        Loaded += async (_, _) =>
        {
            _tray = new AppTrayIcon(ShowFromTray, RequestExit);
            await ApplyOverlayVisibilityAsync();
        };
        _initialized = true;
        try
        {
            var settings = UserSettings.Load();
            BaseUrl.Text = settings.BaseUrl;
            ModelId.Text = settings.Model;
            PeriodicInterval.Text = settings.IntervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            LocalFiles.Log("settings-load", ex);
            BaseUrl.Text = AiClient.DefaultBaseUrl;
            Status("Настройки не загрузились. Восстановлены значения по умолчанию.", error: true);
        }

        UpdateControls();
        UpdateStats();
    }

    internal event Action<bool>? AnswerScrollRequested;

    internal void BindOverlaySettings(OverlayViewModel model) => OverlayOptions.DataContext = model;

    internal void WarnOverlayCaptureInclusion() =>
        Status("Плавающее окно не удалось исключить из захвата. Включите плотный фон или перенесите окно.",
            error: true);

    private void ResetOverlayPosition_Click(object sender, RoutedEventArgs e)
    {
        if (OverlayOptions.DataContext is OverlayViewModel model)
            model.ResetPosition();
    }

    internal void DisableOverlay() => OverlayEnabled.IsChecked = false;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WndProc);
        _hotkeys = new HotkeyManager(hwnd, id => _ = OnHotkeyAsync(id));
        foreach (var (id, modifiers, vk, label) in AppHotkeys.Defaults)
            TryRegister(id, modifiers, vk, label);
    }

    private void TryRegister(int id, uint modifiers, uint vk, string label)
    {
        if (_hotkeys is null || _hotkeys.Register(id, modifiers, vk))
            return;
        Status($"Горячая клавиша {label} занята.", error: true);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == HotkeyManager.WmHotkey)
        {
            _hotkeys?.Handle(wParam);
            handled = true;
        }

        return 0;
    }

    private async Task OnHotkeyAsync(int id)
    {
        try
        {
            switch (id)
            {
                case AppHotkeys.Overlay:
                    await OnUiAsync(() => OverlayEnabled.IsChecked = OverlayEnabled.IsChecked != true);
                    break;
                case AppHotkeys.Scroll:
                    await AnalyzeScrollAsync();
                    break;
                case AppHotkeys.AnswerPageUp:
                case AppHotkeys.AnswerPageDown:
                    await OnUiAsync(() => AnswerScrollRequested?.Invoke(id == AppHotkeys.AnswerPageDown));
                    break;
                case AppHotkeys.ShowWindow:
                    await OnUiAsync(ShowFromTray);
                    break;
            }
        }
        catch (Exception ex)
        {
            LocalFiles.Log("hotkey", ex);
            await OnUiAsync(() => Status("Горячая клавиша завершилась ошибкой. Подробности в журнале.", error: true));
        }
    }

    private async void OverlayToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initialized)
            await ApplyOverlayVisibilityAsync();
    }

    private async Task ApplyOverlayVisibilityAsync()
    {
        var enabled = OverlayEnabled.IsChecked == true;
        try
        {
            await _overlay.SetVisibleAsync(enabled);
            _persistOverlayEnabled(enabled);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            LocalFiles.Log("overlay-window", ex);
            await OnUiAsync(() =>
            {
                OverlayEnabled.IsChecked = false;
                Status("Не удалось открыть плавающее окно.", error: true);
            });
        }
    }

    private void BaseUrl_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_initialized)
            return;

        AiClient.TryParseBaseUrl(BaseUrl.Text, out var endpoint);
        if (endpoint == _keyEndpoint)
            return;

        _keyEndpoint = endpoint;
        ApiKey.Clear();
        if (endpoint is null)
            return;

        try { ApiKey.Password = KeyStore.Read(endpoint); }
        catch (Exception ex)
        {
            LocalFiles.Log("key-load", ex);
            Status("Ключ не удалось прочитать. Введите его заново.", error: true);
        }
    }

    private async void Pick_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync("picker", async token =>
        {
            await OnUiAsync(() => Status("Выберите монитор в системном окне…"));
            var item = await GraphicsCapture.PickAsync(new WindowInteropHelper(this).Handle, token);
            token.ThrowIfCancellationRequested();
            if (item is null)
            {
                await OnUiAsync(() => Status("Выбор источника отменён."));
                return;
            }

            await OnUiAsync(() =>
            {
                DetachSource();
                _source = item;
                item.Closed += Source_Closed;
                SourceText.Text = item.DisplayName;
                ClearPreview();
                Status("Источник выбран.");
            });
        });

    private async void Borderless_Click(object sender, RoutedEventArgs e)
    {
        if (_borderlessCapture)
        {
            _borderlessCapture = false;
            BorderlessButton.Content = "Запросить захват без рамки";
            Status("Захват с системной рамкой.");
            return;
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            Status("Для захвата без рамки нужна пакетная версия на Windows 11.", error: true);
            return;
        }

        await RunOperationAsync("borderless-consent", async token =>
        {
            try
            {
                var access = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask(token);
                _borderlessCapture = access == Windows.Security.Authorization.AppCapabilityAccess.AppCapabilityAccessStatus.Allowed;
                if (_borderlessCapture)
                    BorderlessButton.Content = "Вернуть системную рамку";
                Status(_borderlessCapture ? "Windows разрешила захват без рамки."
                    : "Windows не разрешила захват без рамки.", error: !_borderlessCapture);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException)
            {
                LocalFiles.Log("borderless-consent", ex);
                Status("Для захвата без рамки запустите установленную MSIX-версию.", error: true);
            }
        });
    }

    private async void Catalog_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync("catalog", async token =>
        {
            await OnUiAsync(() => Status("Загружаем каталог AI Tunnel…"));
            var current = await OnUiAsync(() => ModelId.Text);
            var models = await _ai.ListVisionModelsAsync(token);
            token.ThrowIfCancellationRequested();
            await OnUiAsync(() =>
            {
                ModelId.ItemsSource = models;
                ModelId.Text = current;
                Status(models.Length == 0
                    ? "В каталоге нет моделей для изображений. Можно ввести ID вручную."
                    : $"Моделей с поддержкой изображений: {models.Length}. Откройте список «Модель».");
            });
        });

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadSettings(out var settings, out var endpoint))
            return;
        try
        {
            KeyStore.Save(endpoint, ApiKey.Password.Trim());
            settings.Save();
            Status("Настройки сохранены.");
        }
        catch (Exception ex)
        {
            LocalFiles.Log("settings-save", ex);
            Status("Не удалось сохранить настройки. Проверьте доступ к профилю Windows.", error: true);
        }
    }

    private async Task<bool> ReplaceOperationAsync()
    {
        var version = ++_requestVersion;
        while (_operation is { } previous && _operationFinished is { } finished)
        {
            previous.Cancel();
            await finished.Task;
            if (version != _requestVersion || _closing || _shutdownComplete)
                return false;
        }

        return !_closing && !_shutdownComplete;
    }

    private void AddAnswer(string model, string result, bool selectAnswerTab = true)
    {
        _answers++;
        UpdateStats();
        if (selectAnswerTab)
            AnswerTab.IsSelected = true;
        AnswerText.Text = result;
        AnswerScroll.ScrollToTop();
        _history.Insert(0, new HistoryEntry(DateTimeOffset.Now, model, result));
        if (_history.Count > 200)
            _history.RemoveAt(_history.Count - 1);
        HistoryEmpty.Visibility = Visibility.Collapsed;
        HistoryList.SelectedIndex = 0;
    }

    private async void StartPeriodic_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is not null || _periodic is not null || _source is not { } source
            || !ReadSettings(out var settings, out var endpoint))
            return;

        var key = ApiKey.Password.Trim();
        if (key.Length == 0 || string.IsNullOrWhiteSpace(settings.Model))
        {
            Status("Выберите модель и введите ключ.", error: true);
            return;
        }
        if (!int.TryParse(PeriodicInterval.Text, out var seconds) || seconds is < 5 or > 3600)
        {
            Status("Интервал должен быть от 5 до 3600 секунд.", error: true);
            return;
        }

        _periodic = new(source, endpoint, key, settings.Model, TimeSpan.FromSeconds(seconds));
        _lastAnalyzedFrame = null;
        await AnalyzePeriodicFrameAsync();
    }

    private async Task AnalyzePeriodicFrameAsync()
    {
        _periodicTimer.Stop();
        if (_periodic is not { } session || _operation is not null || _closing || _shutdownComplete)
            return;

        await RunOperationAsync("periodic", async token =>
        {
            Status("Периодический анализ · получаем кадр…");
            var frame = await CaptureAsync(session.Source, token);
            if (frame.Fingerprint == _lastAnalyzedFrame)
            {
                Status("Кадр не изменился. Ждём следующий снимок.");
                return;
            }

            _requests++;
            UpdateStats();
            Status("Периодический анализ · ждём ответ…");
            await ShowAnalysisAsync(session.Model,
                ct => _ai.AnalyzeAsync(session.Endpoint, session.Key, session.Model, frame.Payload, ct),
                frame.Fingerprint, "Ответ готов. Периодический анализ продолжается.", token);
        }, usesSource: true);
    }

    private async Task ShowAnalysisAsync(string model, Func<CancellationToken, Task<string>> analyze,
        string? lastFrame, string status, CancellationToken token)
    {
        var requestId = await _overlay.BeginAsync("Анализируем изображение…");
        try
        {
            var result = MathText.Format(await analyze(token));
            token.ThrowIfCancellationRequested();
            await _overlay.CompleteAsync(requestId, result);
            token.ThrowIfCancellationRequested();
            _lastAnalyzedFrame = lastFrame;
            AddAnswer(model, result, selectAnswerTab: false);
            Status(status);
        }
        catch (AiException ex)
        {
            await _overlay.FailAsync(requestId, ex.Message);
            throw;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await _overlay.EndAsync("Запрос отменён.");
            throw;
        }
    }

    private void SchedulePeriodicFrame()
    {
        _periodicTimer.Stop();
        if (_periodic is not { } session || _operation is not null || _closing || _shutdownComplete)
            return;

        var delay = _retryDelay > session.Interval ? _retryDelay : session.Interval;
        _periodicTimer.Interval = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds, int.MaxValue));
        _periodicTimer.Start();
    }

    private void StopPeriodic()
    {
        _periodicTimer.Stop();
        _periodic = null;
        _retryDelay = TimeSpan.Zero;
    }

    private async Task AnalyzeScrollAsync()
    {
        if (_closing || _shutdownComplete)
            return;
        if (_source is not { } source)
        {
            ShowFromTray();
            Status("Сначала выберите источник захвата.", error: true);
            return;
        }
        if (!ReadSettings(out var settings, out var endpoint))
        {
            ShowFromTray();
            return;
        }
        var key = ApiKey.Password.Trim();
        if (key.Length == 0 || string.IsNullOrWhiteSpace(settings.Model))
        {
            ShowFromTray();
            Status("Выберите модель и введите ключ.", error: true);
            return;
        }

        if (!await ReplaceOperationAsync())
            return;

        if (!Equals(_source, source))
        {
            ShowFromTray();
            Status("Источник захвата изменился. Запустите анализ заново.", error: true);
            return;
        }

        await RunOperationAsync("scroll", async token =>
        {
            await OnUiAsync(() => Status("Через 3 секунды начнётся сбор. Перейдите к началу страницы."));
            await Task.Delay(TimeSpan.FromSeconds(3), token);
            var frames = new List<ImagePayload>();
            var fingerprints = new HashSet<string>();
            string? lastFrame = null;
            var bytes = 0;
            for (var i = 0; i < 8; i++)
            {
                await OnUiAsync(() => Status($"Медленно прокручивайте страницу · снимок {i + 1} из 8."));
                var frame = await CaptureAsync(source, token);
                if (fingerprints.Add(frame.Fingerprint))
                {
                    if (frames.Count > 0 && bytes + frame.Payload.Bytes.Length > 20 * 1024 * 1024)
                        break;

                    frames.Add(frame.Payload);
                    bytes += frame.Payload.Bytes.Length;
                }
                lastFrame = frame.Fingerprint;
                if (i < 7)
                    await Task.Delay(TimeSpan.FromSeconds(2), token);
            }

            await OnUiAsync(() =>
            {
                _requests++;
                UpdateStats();
                Status($"Анализируем: кадров {frames.Count}…");
            });
            await ShowAnalysisAsync(settings.Model,
                ct => Task.Run(() => _ai.AnalyzeScrollAsync(endpoint, key, settings.Model, frames, ct), ct),
                lastFrame, $"Ответ по серии готов · кадров {frames.Count}.", token);
        }, usesSource: true);
    }

    private async Task<CapturedImage> CaptureAsync(GraphicsCaptureItem source, CancellationToken token)
    {
        await OnUiAsync(() =>
        {
            CaptureStatus.Text = "Идёт захват";
            CaptureLight.Fill = Active;
        });
        try
        {
            var image = await GraphicsCapture.TakeOnceAsync(source, null, token, _borderlessCapture);

            token.ThrowIfCancellationRequested();
            await OnUiAsync(() =>
            {
                PreviewImage.Source = FrameEncoding.Decode(image.Payload);
                PreviewEmpty.Visibility = Visibility.Collapsed;
                FrameDetails.Text = $"{image.Width} × {image.Height}   ·   {DateTime.Now:HH:mm:ss}";
            });
            return image;
        }
        finally
        {
            await OnUiAsync(() =>
            {
                CaptureStatus.Text = "Захват выключен";
                CaptureLight.Fill = Idle;
            });
        }
    }

    private async Task RunOperationAsync(string name, Func<CancellationToken, Task> action,
        bool usesSource = false)
    {
        if (_operation is not null || _closing || _shutdownComplete)
            return;

        _periodicTimer.Stop();
        if (name == "periodic")
            _retryDelay = TimeSpan.Zero;
        using var cancellation = new CancellationTokenSource();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _operationFinished = finished;
        _operation = cancellation;
        _operationUsesSource = usesSource;
        _sourceLost = false;
        await OnUiAsync(UpdateControls);
        Task ReportErrorAsync(string message) => OnUiAsync(() =>
        {
            if (name == "scroll" && !_closing)
                ShowFromTray();
            Status(message, error: true);
        });

        try { await action(cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            var message = _sourceLost ? "Источник захвата закрыт. Выберите экран повторно."
                : "Операция остановлена.";
            if (_sourceLost)
                await ReportErrorAsync(message);
            else
                await OnUiAsync(() => Status(message));
        }
        catch (CaptureException ex)
        {
            LocalFiles.Log(name, ex);
            await ReportErrorAsync(ex.Message);
        }
        catch (AiException ex)
        {
            if (name == "periodic" && ex.RequiresConfigurationChange)
                StopPeriodic();
            else if (ex.RetryAfter is { } delay && delay > _retryDelay)
                _retryDelay = delay;
            LocalFiles.Log(name, ex);
            await ReportErrorAsync(ex.Message);
        }
        catch (Exception ex)
        {
            LocalFiles.Log(name, ex);
            const string message = "Операция завершилась ошибкой. Подробности в журнале.";
            await ReportErrorAsync(message);
        }
        finally
        {
            _operation = null;
            _operationFinished = null;
            try
            {
                await OnUiAsync(() =>
                {
                    UpdateControls();
                    SchedulePeriodicFrame();
                });
                if (_closing)
                    await FinishCloseAsync();
            }
            finally { finished.TrySetResult(); }
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _requestVersion++;
        StopPeriodic();
        _operation?.Cancel();
        Status(_operation is null ? "Анализ остановлен." : "Останавливаем операцию…");
        UpdateControls();
    }

    private void Source_Closed(GraphicsCaptureItem item, object args) =>
        Dispatcher.InvokeAsync(() =>
        {
            if (_source is null || !Equals(item, _source))
                return;

            StopPeriodic();
            _sourceLost = _operationUsesSource && _operation is not null;
            if (_sourceLost)
                _operation?.Cancel();
            DetachSource();
            ClearPreview();
            SourceText.Text = "Источник закрыт. Выберите монитор.";
            Status("Доступ к источнику захвата потерян.", error: true);
            UpdateControls();
        });

    private void ClearPreview()
    {
        PreviewImage.Source = null;
        PreviewEmpty.Visibility = Visibility.Visible;
        FrameDetails.Text = "";
    }

    private void DetachSource()
    {
        if (_source is { } source)
            source.Closed -= Source_Closed;
        _source = null;
    }

    private void History_Changed(object sender, SelectionChangedEventArgs e)
    {
        HistoryText.Text = (HistoryList.SelectedItem as HistoryEntry)?.Text ?? "";
        HistoryScroll.ScrollToTop();
    }

    private bool ReadSettings(out UserSettings settings, out Uri endpoint)
    {
        settings = new();
        endpoint = null!;
        if (!AiClient.TryParseBaseUrl(BaseUrl.Text, out var parsed))
        {
            Status("Введите HTTPS Base URL API, например https://api.aitunnel.ru/v1.", error: true);
            BaseUrl.Focus();
            return false;
        }
        endpoint = parsed!;
        settings = new UserSettings
        {
            BaseUrl = endpoint.AbsoluteUri.TrimEnd('/'),
            Model = ModelId.Text.Trim(),
            IntervalSeconds = int.TryParse(PeriodicInterval.Text, out var interval) && interval is >= 5 and <= 3600
                ? interval : 30
        };
        return true;
    }

    private void UpdateControls()
    {
        var idle = _operation is null;
        PickButton.IsEnabled = idle && _periodic is null;
        SettingsPanel.IsEnabled = idle;
        StartPeriodicButton.IsEnabled = idle && _periodic is null && _source is not null;

        StopButton.IsEnabled = _periodic is not null || _operation is { IsCancellationRequested: false };
    }

    private void UpdateStats() =>
        StatsText.Text = $"Запросы {_requests}   ·   Ответы {_answers}";

    private void Status(string text, bool error = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = error ? Error : Foreground;
    }

    private Task OnUiAsync(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return Dispatcher.InvokeAsync(action, DispatcherPriority.Normal).Task;
    }

    private Task<T> OnUiAsync<T>(Func<T> action)
    {
        if (Dispatcher.CheckAccess())
            return Task.FromResult(action());

        return Dispatcher.InvokeAsync(action, DispatcherPriority.Normal).Task;
    }

    private void HideToTray()
    {
        WindowState = WindowState.Normal;
        Hide();
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    private void RequestExit()
    {
        StopPeriodic();
        _exitRequested = true;
        if (_operation is not null)
        {
            _closing = true;
            _operation.Cancel();
            return;
        }

        _ = FinishCloseAsync();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_shutdownComplete)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        if (!_exitRequested)
        {
            HideToTray();
            return;
        }

        if (_operation is not null)
        {
            _closing = true;
            _operation.Cancel();
            return;
        }

        _ = FinishCloseAsync();
    }

    private async Task FinishCloseAsync()
    {
        if (_shutdownComplete)
            return;

        _closing = true;
        StopPeriodic();
        _exitRequested = true;
        DetachSource();
        _hotkeys?.Dispose();
        _hotkeys = null;
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
        _tray?.Dispose();
        _tray = null;
        try { await _overlay.ShutdownAsync(); }
        catch (Exception ex) { LocalFiles.Log("overlay-shutdown", ex); }

        _shutdownComplete = true;
        await OnUiAsync(Close);
    }

    private sealed record PeriodicAnalysis(GraphicsCaptureItem Source, Uri Endpoint, string Key, string Model, TimeSpan Interval);

    private sealed record HistoryEntry(DateTimeOffset Time, string Model, string Text)
    {
        public string Label => $"{Time:HH:mm:ss}   {Model}";
    }
}
