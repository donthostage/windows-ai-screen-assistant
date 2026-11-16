using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ScreenAssistant.Configuration;
using ScreenAssistant.Core.AIClient;
using ScreenAssistant.Core.Geometry;
using ScreenAssistant.Core.Overlay;
using ScreenAssistant.Core.Scheduler;
using ScreenAssistant.Hotkeys;
using ScreenAssistant.Licensing;
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
    private const int GwlExstyle = -20;
    private const int WsExAppWindow = 0x00040000;
    private const int WsExToolWindow = 0x00000080;

    private readonly AiClient _ai;
    private readonly Core.Licensing.ActivationClient _activation;
    private readonly IOverlayService _overlay;
    private readonly Action<bool> _persistOverlayEnabled;
    private readonly ObservableCollection<HistoryEntry> _history = new();
    private GraphicsCaptureItem? _source;
    private string? _approvedCaptureDestination;
    private bool _confirmingCapture;
    private Uri? _keyEndpoint;
    private CancellationTokenSource? _operation;
    private TaskCompletionSource? _operationFinished;
    private long _requestVersion;
    private AnalysisScheduler? _scheduler;
    private CancellationTokenSource? _periodicCts;
    private CaptureSession? _periodicCapture;
    private PeriodicAnalysis? _periodic;
    private string? _lastAnalyzedFrame;
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
    private bool _keyVisible;
    private int _requests;
    private int _answers;

    internal MainWindow(AiClient ai, Core.Licensing.ActivationClient activation, IOverlayService overlay,
        bool overlayEnabled, Action<bool> persistOverlayEnabled)
    {
        _ai = ai;
        _activation = activation;
        _overlay = overlay;
        _persistOverlayEnabled = persistOverlayEnabled;
        InitializeComponent();
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
            _tray ??= new AppTrayIcon(ShowFromTray, RequestExit, StopFromTray);
            await ApplyOverlayVisibilityAsync();
            HideToTray();
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

    internal event Action? AnswerAutoscrollToggled;
    private CropRect? _nextCrop;
    internal event Action<bool>? AnswerScrollRequested;

    internal void BindOverlaySettings(OverlayViewModel model) => OverlayOptions.DataContext = model;

    internal void WarnOverlayCaptureInclusion() =>
        Status("Оверлей виден в записи. Включите плотный фон", error: true);

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
        var style = GetWindowLong(hwnd, GwlExstyle);
        style = (style | WsExToolWindow) & ~WsExAppWindow;
        SetWindowLong(hwnd, GwlExstyle, style);
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
                case AppHotkeys.ToggleAnswerAutoscroll:
                    await OnUiAsync(() => AnswerAutoscrollToggled?.Invoke());
                    break;
                case AppHotkeys.Panic:
                    _requestVersion++;
                    _operation?.Cancel();
                    _periodicCts?.Cancel();
                    try { await _overlay.HideForAsync(TimeSpan.FromSeconds(3)); }
                    catch (Exception ex) { LocalFiles.Log("panic-hide", ex); }
                    await OnUiAsync(() => Status("Паника: оверлей скрыт, текущий запрос отменён."));
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
            if (enabled && !await _overlay.EnsureExcludedAsync())
                await OnUiAsync(() => Status("Оверлей виден в записи. Включите плотный фон", error: true));
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
        ApiKeyPlain.Text = "";
        if (endpoint is null)
            return;

        try
        {
            var key = KeyStore.Read(endpoint);
            ApiKey.Password = key;
            ApiKeyPlain.Text = key;
        }
        catch (Exception ex)
        {
            LocalFiles.Log("key-load", ex);
            Status("Ключ не удалось прочитать. Введите его заново.", error: true);
        }
    }

    private void ShowKey_Click(object sender, RoutedEventArgs e)
    {
        _keyVisible = !_keyVisible;
        if (_keyVisible)
        {
            ApiKeyPlain.Text = ApiKey.Password;
            ApiKey.Visibility = Visibility.Collapsed;
            ApiKeyPlain.Visibility = Visibility.Visible;
            ShowKeyButton.Content = "скрыть";
        }
        else
        {
            ApiKey.Password = ApiKeyPlain.Text;
            ApiKeyPlain.Visibility = Visibility.Collapsed;
            ApiKey.Visibility = Visibility.Visible;
            ShowKeyButton.Content = "показать";
        }
    }

    private string CurrentApiKey() =>
        _keyVisible ? ApiKeyPlain.Text.Trim() : ApiKey.Password.Trim();

    private void PeriodicInterval_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !Regex.IsMatch(e.Text, "^[0-9]+$");

    private void PeriodicInterval_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PeriodicInterval.Text, out var seconds))
        {
            PeriodicInterval.BorderBrush = Error;
            return;
        }

        seconds = Math.Clamp(seconds, 5, 3600);
        PeriodicInterval.Text = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        PeriodicInterval.ClearValue(BorderBrushProperty);
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

    private void Crop_Click(object sender, RoutedEventArgs e)
    {
        if (_source is null)
        {
            Status("Сначала выберите источник захвата.", error: true);
            return;
        }

        SelectNextCrop();
    }

    private void SelectNextCrop()
    {
        var picker = new CropSelectionWindow();
        if (picker.ShowDialog() != true)
            return;

        var relative = MonitorGeometry.ToMonitorRelative(picker.SelectedPixels, picker.SelectedMonitor);
        if (relative.IsEmpty)
        {
            Status("Слишком маленькая область", error: true);
            return;
        }

        _nextCrop = relative;
        FrameDetails.Text = $"область {relative.Width} × {relative.Height} · {relative.X}, {relative.Y}";
        Status("Область выбрана для следующего кадра.");
    }

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
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
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
            KeyStore.Save(endpoint, CurrentApiKey());
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

    private void ActivateLicense_Click(object sender, RoutedEventArgs e) =>
        new Licensing.ActivationWindow(_activation) { Owner = this }.ShowDialog();

    private async void StartPeriodic_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is not null || _periodic is not null || _scheduler is not null || _source is not { } source)
            return;
        if (!int.TryParse(PeriodicInterval.Text, out var seconds) || seconds is < 5 or > 3600)
        {
            PeriodicInterval.BorderBrush = Error;
            Status("Интервал должен быть от 5 до 3600 секунд.", error: true);
            return;
        }
        if (!TryLoadLicense(out var license))
            return;

        string model;
        Func<ImagePayload, CancellationToken, Task<string>> analyze;
        UserSettings settings;
        if (license is not null)
        {
            var server = new Uri(license.Server);
            if (!ConfirmCaptureDestination(source, server, licensed: true))
                return;

            settings = UserSettings.Load();
            model = "Сервер лицензий";
            analyze = (image, token) => RunLicensedAnalysisAsync(license, key =>
                _activation.AnalyzeAsync(server, license.LicenseId,
                    Guid.NewGuid(), key, image, token));
        }
        else
        {
            if (!ReadSettings(out settings, out var endpoint))
                return;

            var key = CurrentApiKey();
            model = settings.Model;
            if (key.Length == 0 || string.IsNullOrWhiteSpace(model))
            {
                Status("Выберите модель и введите ключ.", error: true);
                return;
            }
            if (!ConfirmCaptureDestination(source, endpoint, licensed: false))
                return;

            analyze = (image, token) => _ai.AnalyzeAsync(endpoint, key, model, image, token);
            try
            {
                KeyStore.Save(endpoint, key);
                settings.Save();
            }
            catch (Exception ex)
            {
                LocalFiles.Log("settings-autosave", ex);
            }
        }

        var session = new PeriodicAnalysis(source, model, analyze,
            TimeSpan.FromSeconds(seconds), TimeSpan.FromMinutes(settings.DurationMinutes));
        _lastAnalyzedFrame = null;
        _periodic = session;
        _scheduler = new AnalysisScheduler();
        _periodicCts = new CancellationTokenSource();
        var token = _periodicCts.Token;
        UpdateControls();
        var capture = new CaptureSession();
        try
        {
            await capture.StartAsync(source, token, _borderlessCapture);
            token.ThrowIfCancellationRequested();
        }
        catch (Exception ex)
        {
            await capture.DisposeAsync();
            if (ReferenceEquals(_periodic, session))
            {
                StopPeriodic();
                UpdateControls();
                if (ex is not OperationCanceledException)
                {
                    LocalFiles.Log("periodic-start", ex);
                    Status("Не удалось запустить захват экрана.", error: true);
                }
            }
            return;
        }
        _periodicCapture = capture;
        UpdateControls();
        Status("Периодический анализ запущен.");
        _ = RunPeriodicLoopAsync(session, token);
    }

    private async Task RunPeriodicLoopAsync(PeriodicAnalysis session, CancellationToken token)
    {
        var scheduler = _scheduler!;
        try
        {
            var end = await scheduler.RunAsync(session.Interval, session.Duration,
                ct => OnUiAsync(() => RunPeriodicOperationAsync(session, ct)).Unwrap(), token);
            await OnUiAsync(() =>
            {
                if (!ReferenceEquals(_scheduler, scheduler))
                    return;
                if (end == SessionEnd.DurationElapsed)
                    Status("Время тренировки истекло.");
                else if (!_closing)
                    Status("Тренировка остановлена.");
            });
        }
        catch (Exception ex)
        {
            LocalFiles.Log("periodic-loop", ex);
            await OnUiAsync(() =>
            {
                if (ReferenceEquals(_scheduler, scheduler))
                    Status("Периодический анализ завершился ошибкой.", error: true);
            });
        }
        finally
        {
            await OnUiAsync(() =>
            {
                if (ReferenceEquals(_scheduler, scheduler))
                    StopPeriodic();
                UpdateControls();
            });
        }
    }

    private async Task<TimeSpan?> RunPeriodicOperationAsync(PeriodicAnalysis session, CancellationToken token)
    {
        if (token.IsCancellationRequested || !ReferenceEquals(_periodic, session))
            return null;

        TimeSpan? retryAfter = null;
        await RunOperationAsync("periodic", async operationToken =>
        {
            retryAfter = await AnalyzePeriodicFrameAsync(session, operationToken);
        }, usesSource: true, sessionToken: token);
        return retryAfter;
    }

    private async Task<TimeSpan?> AnalyzePeriodicFrameAsync(PeriodicAnalysis session, CancellationToken token)
    {
        if (_closing || _shutdownComplete)
            return null;

        await OnUiAsync(() => Status("Периодический анализ · получаем кадр…"));
        var frame = await CaptureAsync(session.Source, token);
        if (frame.Fingerprint == _lastAnalyzedFrame)
        {
            await OnUiAsync(() => Status("Кадр не изменился. Ждём следующий снимок."));
            return null;
        }

        await OnUiAsync(() =>
        {
            _requests++;
            UpdateStats();
            Status("Периодический анализ · ждём ответ…");
        });

        try
        {
            await ShowAnalysisAsync(session.Model,
                ct => session.Analyze(frame.Payload, ct),
                frame.Fingerprint, "Ответ готов. Периодический анализ продолжается.", token);
            return null;
        }
        catch (AiException ex)
        {
            await OnUiAsync(() => Status(ex.Message, error: true));
            if (ex.RequiresConfigurationChange)
            {
                await OnUiAsync(StopPeriodic);
                throw;
            }

            return ex.RetryAfter;
        }
    }

    private async Task ShowAnalysisAsync(string model, Func<CancellationToken, Task<string>> analyze,
        string? lastFrame, string status, CancellationToken token)
    {
        long requestId;
        try
        {
            requestId = await _overlay.BeginAsync("Анализируем изображение…");
        }
        catch (InvalidOperationException ex)
        {
            LocalFiles.Log("overlay-begin", ex);
            await OnUiAsync(() => Status("Оверлей закрыт. Включите плавающий ответ.", error: true));
            throw;
        }

        try
        {
            var result = MathText.Format(await analyze(token));
            token.ThrowIfCancellationRequested();
            await _overlay.CompleteAsync(requestId, result);
            token.ThrowIfCancellationRequested();
            _lastAnalyzedFrame = lastFrame;
            await OnUiAsync(() =>
            {
                AddAnswer(model, result, selectAnswerTab: false);
                Status(status);
            });
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

    private void StopPeriodic()
    {
        try { _periodicCts?.Cancel(); }
        catch { /* ignore */ }
        _periodicCts?.Dispose();
        _periodicCts = null;
        _scheduler = null;
        _periodic = null;
        if (_periodicCapture is { } capture)
        {
            _periodicCapture = null;
            _ = capture.DisposeAsync().AsTask();
        }
    }

    private void StopFromTray() => Dispatcher.Invoke(() =>
    {
        _requestVersion++;
        StopPeriodic();
        _operation?.Cancel();
        Status(_operation is null ? "Анализ остановлен." : "Останавливаем операцию…");
        UpdateControls();
    });

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
        if (!TryLoadLicense(out var license))
            return;
        string model;
        Func<IReadOnlyList<ImagePayload>, CancellationToken, Task<string>> analyze;
        if (license is not null)
        {
            var server = new Uri(license.Server);
            if (!ConfirmCaptureDestination(source, server, licensed: true))
                return;

            model = "Сервер лицензий";
            analyze = (frames, token) => RunLicensedAnalysisAsync(license, key =>
                _activation.AnalyzeSeriesAsync(server, license.LicenseId,
                    Guid.NewGuid(), key, frames, token));
        }
        else
        {
            if (!ReadSettings(out var settings, out var endpoint))
            {
                ShowFromTray();
                return;
            }
            var key = CurrentApiKey();
            model = settings.Model;
            if (key.Length == 0 || string.IsNullOrWhiteSpace(model))
            {
                ShowFromTray();
                Status("Выберите модель и введите ключ.", error: true);
                return;
            }
            if (!ConfirmCaptureDestination(source, endpoint, licensed: false))
                return;

            analyze = (frames, token) => _ai.AnalyzeScrollAsync(endpoint, key, model, frames, token);
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
            await ShowAnalysisAsync(model,
                ct => Task.Run(() => analyze(frames, ct), ct),
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
            CropRect? crop = null;
            await OnUiAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                crop = _nextCrop;
                _nextCrop = null;
            });
            CapturedImage image;
            if (_periodicCapture is { } capture)
                image = await capture.TakeAsync(crop, token);
            else
                image = await GraphicsCapture.TakeOnceAsync(source, crop, token, _borderlessCapture);

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
        bool usesSource = false, CancellationToken sessionToken = default)
    {
        if (_operation is not null || _closing || _shutdownComplete)
            return;

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
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
            LocalFiles.Log(name, ex);
            await ReportErrorAsync(ex.Message);
        }
        catch (Exception ex)
        {
            LocalFiles.Log(name, ex);
            await ReportErrorAsync("Операция завершилась ошибкой. Подробности в журнале.");
        }
        finally
        {
            _operation = null;
            _operationFinished = null;
            try
            {
                await OnUiAsync(UpdateControls);
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

    private bool ConfirmCaptureDestination(GraphicsCaptureItem source, Uri destination, bool licensed)
    {
        if (_confirmingCapture || _closing || _shutdownComplete || !ReferenceEquals(_source, source))
            return false;

        var approval = (licensed ? "licensed " : "direct ") + destination.AbsoluteUri;
        if (_approvedCaptureDestination == approval)
            return true;

        _confirmingCapture = true;
        try
        {
            var route = licensed ? "Сервер передаёт кадры в AI Tunnel для анализа." : "Кадры отправляются напрямую в API.";
            var message = $"Источник захвата · {source.DisplayName}\n"
                + $"Получатель · {destination.AbsoluteUri}\n\n"
                + $"{route} На кадрах может быть видно всё содержимое выбранного источника. "
                + "Остановка в приложении прекращает отправку новых кадров.\n\n"
                + "Разрешить отправку кадров для анализа?";
            ShowFromTray();
            if (MessageBox.Show(this, message, "Передача кадров", MessageBoxButton.YesNo,
                MessageBoxImage.Information, MessageBoxResult.No) != MessageBoxResult.Yes
                || _closing || _shutdownComplete || !ReferenceEquals(_source, source))
                return false;

            _approvedCaptureDestination = approval;
            return true;
        }
        finally
        {
            _confirmingCapture = false;
        }
    }

    private void DetachSource()
    {
        if (_source is { } source)
            source.Closed -= Source_Closed;
        _source = null;
        _approvedCaptureDestination = null;
        _nextCrop = null;
    }

    private void History_Changed(object sender, SelectionChangedEventArgs e)
    {
        HistoryText.Text = (HistoryList.SelectedItem as HistoryEntry)?.Text ?? "";
        HistoryScroll.ScrollToTop();
    }

    private static async Task<string> RunLicensedAnalysisAsync(LicenseReceipt license,
        Func<System.Security.Cryptography.ECDsa, Task<string>> analyze)
    {
        try
        {
            using var key = WindowsDeviceKey.OpenOrCreate(license.HardwareBacked);
            return await analyze(key);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            LocalFiles.Log("license-device-key", ex);
            throw new AiException("Не удалось открыть ключ устройства.", System.Net.HttpStatusCode.Unauthorized);
        }
    }

    private bool TryLoadLicense(out LicenseReceipt? license)
    {
        try
        {
            license = LicenseReceipt.Load();
            return true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException
            or System.Text.Json.JsonException)
        {
            LocalFiles.Log("license-read", ex);
            Status("Не удалось прочитать лицензию. Обратитесь к оператору.", error: true);
            license = null;
            return false;
        }
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
        var loaded = UserSettings.Load();
        settings = loaded with
        {
            BaseUrl = endpoint.AbsoluteUri.TrimEnd('/'),
            Model = ModelId.Text.Trim(),
            IntervalSeconds = int.TryParse(PeriodicInterval.Text, out var interval) && interval is >= 5 and <= 3600
                ? interval : loaded.IntervalSeconds
        };
        return true;
    }

    private void UpdateControls()
    {
        var idle = _operation is null;
        var periodicIdle = _periodic is null && _scheduler is null;
        PickButton.IsEnabled = idle && periodicIdle;
        CropButton.IsEnabled = idle && periodicIdle && _source is not null;
        SettingsPanel.IsEnabled = idle;
        StartPeriodicButton.IsEnabled = idle && periodicIdle && _source is not null;
        StopButton.IsEnabled = !periodicIdle || _operation is { IsCancellationRequested: false };
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

        return Dispatcher.InvokeAsync(action).Task;
    }

    private Task<T> OnUiAsync<T>(Func<T> action)
    {
        if (Dispatcher.CheckAccess())
            return Task.FromResult(action());

        return Dispatcher.InvokeAsync(action).Task;
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
        await OnUiAsync(() =>
        {
            Close();
            Application.Current.Shutdown();
        });
    }

    private sealed record PeriodicAnalysis(
        GraphicsCaptureItem Source, string Model, Func<ImagePayload, CancellationToken, Task<string>> Analyze,
        TimeSpan Interval, TimeSpan Duration);

    private sealed record HistoryEntry(DateTimeOffset Time, string Model, string Text)
    {
        public string Label => $"{Time:HH:mm:ss}   {Model}";
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(nint hwnd, int index, int value);
}
