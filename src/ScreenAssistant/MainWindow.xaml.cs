using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ScreenAssistant.Configuration;
using ScreenAssistant.Core.AIClient;
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

    private readonly Core.Licensing.ActivationClient _activation;
    private readonly IOverlayService _overlay;
    private readonly Action<bool> _persistOverlayEnabled;
    private readonly ObservableCollection<HistoryEntry> _history = new();
    private GraphicsCaptureItem? _source;
    private string? _approvedCaptureDestination;
    private bool _confirmingCapture;
    private bool _activating;
    private LicenseReceipt? _license;
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
    private int _requests;
    private int _answers;

    internal MainWindow(Core.Licensing.ActivationClient activation, IOverlayService overlay,
        bool overlayEnabled, Action<bool> persistOverlayEnabled)
    {
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
            Activate();
            if (_license is null)
            {
                ActivationCode.Focus();
                Status("Введите код активации, затем выберите источник.");
            }
            else
                Status("Код уже принят. Выберите источник и нажмите «Запустить анализ».");
        };
        _initialized = true;
        try
        {
            var settings = UserSettings.Load();
            PeriodicInterval.Text = settings.IntervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            LocalFiles.Log("settings-load", ex);
            Status("Настройки не загрузились. Восстановлены значения по умолчанию.", error: true);
        }

        RefreshLicenseState();

        UpdateControls();
        UpdateStats();
    }

    internal event Action? AnswerAutoscrollToggled;
    internal event Action<bool>? AnswerScrollRequested;
    internal bool SystemDialogOpen { get; private set; }
    internal event Action? SystemDialogChanged;

    private IDisposable OpenSystemDialog()
    {
        SystemDialogOpen = true;
        Topmost = false;
        SystemDialogChanged?.Invoke();
        return new SystemDialogScope(this);
    }

    private sealed class SystemDialogScope(MainWindow window) : IDisposable
    {
        public void Dispose()
        {
            window.SystemDialogOpen = false;
            window.SystemDialogChanged?.Invoke();
        }
    }

    internal void BindOverlaySettings(OverlayViewModel model) => OverlayOptions.DataContext = model;

    internal void WarnOverlayCaptureInclusion() =>
        Status("Оверлей не скрыт из записи.", error: true);

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
                await OnUiAsync(() => Status("Оверлей не скрыт из записи.", error: true));
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

    private void ShowActivation_Click(object sender, RoutedEventArgs e)
    {
        LicenseReadyPanel.Visibility = Visibility.Collapsed;
        ActivationPanel.Visibility = Visibility.Visible;
        ActivationCode.Focus();
    }

    private void RefreshLicenseState()
    {
        if (!TryLoadLicense(out var license))
            return;

        _license = license;
        var ready = license is not null;
        LicenseReadyPanel.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        ActivationPanel.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        if (ready)
            LicenseReadyText.Text = "Код принят. Можно выбирать источник.";
    }

    private async void Activate_Click(object sender, RoutedEventArgs e)
    {
        if (_activating)
            return;

        if (!BuildConfiguration.TryGetLicenseServer(out var server) || server is null)
        {
            ActivationStatus.Text = "В этой сборке не задан адрес сервера.";
            Status("В этой сборке не задан адрес сервера.", error: true);
            return;
        }

        var code = ActivationCode.Password.Trim();
        if (code.Length != 64 || !code.All(Uri.IsHexDigit))
        {
            ActivationStatus.Text = "Проверьте код активации: нужны 64 шестнадцатеричных символа.";
            Status("Код активации не подошёл.", error: true);
            ActivationCode.Focus();
            return;
        }

        var hardwareBacked = HardwareKey.IsChecked == true;
        _activating = true;
        ActivateButton.IsEnabled = ActivationCode.IsEnabled = HardwareKey.IsEnabled = false;
        ActivationStatus.Text = "Активируем код…";
        try
        {
            using var key = WindowsDeviceKey.OpenOrCreate(hardwareBacked);
            var licenseId = await _activation.ActivateAsync(server, code, key, CancellationToken.None);
            ActivationCode.Clear();
            new LicenseReceipt(server.AbsoluteUri, licenseId, hardwareBacked).Save();
            RefreshLicenseState();
            ActivationStatus.Text = "Код принят.";
            Status("Код принят. Теперь выберите источник.");
            PickButton.Focus();
        }
        catch (CryptographicException ex)
        {
            LocalFiles.Log("license-key", ex);
            HardwareKey.Visibility = Visibility.Visible;
            HardwareKey.IsChecked = false;
            ActivationStatus.Text = hardwareBacked
                ? "Не удалось открыть ключ TPM. Снимите галочку и активируйте ещё раз."
                : "Не удалось открыть ключ устройства.";
            Status(ActivationStatus.Text, error: true);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
        {
            ActivationStatus.Text = "Сервер отклонил код. Проверьте его или обратитесь к оператору.";
            Status(ActivationStatus.Text, error: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException)
        {
            LocalFiles.Log("license-activate", ex);
            ActivationStatus.Text = "Не удалось подтвердить код. Проверьте подключение к серверу.";
            Status(ActivationStatus.Text, error: true);
        }
        finally
        {
            _activating = false;
            ActivateButton.IsEnabled = ActivationCode.IsEnabled = HardwareKey.IsEnabled = true;
        }
    }

    private async void Balance_Click(object sender, RoutedEventArgs e)
    {
        if (_activating || _license is not { } receipt)
            return;

        if (!BuildConfiguration.TryGetLicenseServer(out var server) || server is null)
        {
            Status("В этой сборке не задан адрес сервера.", error: true);
            return;
        }

        _activating = true;
        BalanceButton.IsEnabled = false;
        try
        {
            Status("Проверяем доступ…");
            using var key = WindowsDeviceKey.OpenOrCreate(receipt.HardwareBacked);
            var balance = await _activation.ReadAvailableCreditsAsync(server, receipt.LicenseId, key, CancellationToken.None);
            var text = balance is { } credits
                ? $"Доступ открыт. Кредитов: {credits}."
                : "Сервер не подтвердил лицензию.";
            LicenseReadyText.Text = text;
            Status(text, error: balance is null);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
            or JsonException or CryptographicException or HttpRequestException or OperationCanceledException)
        {
            LocalFiles.Log("license-balance", ex);
            Status("Не удалось проверить доступ. Проверьте подключение к серверу.", error: true);
        }
        finally
        {
            _activating = false;
            BalanceButton.IsEnabled = true;
        }
    }

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
            var restoreOverlay = false;
            await OnUiAsync(() =>
            {
                restoreOverlay = OverlayEnabled.IsChecked == true;
                Status("В системном окне выберите Chrome и нажмите ОК.");
            });
            GraphicsCaptureItem? item;
            try
            {
                using var systemDialog = OpenSystemDialog();
                if (restoreOverlay)
                    await _overlay.SetVisibleAsync(false);
                item = await GraphicsCapture.PickAsync(new WindowInteropHelper(this).Handle, token);
            }
            finally
            {
                if (restoreOverlay && OverlayEnabled.IsChecked == true)
                    await _overlay.SetVisibleAsync(true);
            }

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
                var name = string.IsNullOrWhiteSpace(item.DisplayName) ? "окно без имени" : item.DisplayName;
                SourceText.Text = $"Выбран источник: {name}";
                NextStepText.Text = "Следующий шаг — «Запустить анализ».";
                NextStepText.FontWeight = FontWeights.SemiBold;
                NextStepText.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0x4F, 0xAD));
                StartCue.Visibility = Visibility.Visible;
                ClearPreview();
                Status($"Источник «{name}» выбран. Нажмите «Запустить анализ».");
                UpdateControls();
                Activate();
                StartPeriodicButton.Focus();
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
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
            {
                LocalFiles.Log("borderless-consent", ex);
                Status("Для захвата без рамки запустите установленную MSIX-версию.", error: true);
            }
        });
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
        if (_operation is not null || _periodic is not null || _scheduler is not null)
            return;
        if (_source is not { } source)
        {
            Status("Сначала выберите источник.", error: true);
            PickButton.Focus();
            return;
        }
        if (!int.TryParse(PeriodicInterval.Text, out var seconds) || seconds is < 5 or > 3600)
        {
            PeriodicInterval.BorderBrush = Error;
            Status("Интервал должен быть от 5 до 3600 секунд.", error: true);
            return;
        }
        if (!TryLoadLicense(out var license))
            return;
        if (license is null || !BuildConfiguration.TryGetLicenseServer(out var server) || server is null)
        {
            Status("Сначала введите код активации.", error: true);
            ShowActivation_Click(this, e);
            return;
        }

        if (!ConfirmCaptureDestination(source, server))
        {
            Status("Отправка кадров отменена. Источник по-прежнему выбран.");
            return;
        }

        UserSettings settings;
        try
        {
            settings = UserSettings.Load() with { IntervalSeconds = seconds };
            settings.Save();
        }
        catch (Exception ex)
        {
            LocalFiles.Log("settings-autosave", ex);
            settings = new() { IntervalSeconds = seconds };
        }

        var model = "Сервер лицензий";
        Func<ImagePayload, CancellationToken, Task<string>> analyze = (image, token) =>
            RunLicensedAnalysisAsync(license, key =>
                _activation.AnalyzeAsync(server, license.LicenseId, Guid.NewGuid(), key, image, token));

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
        var sourceName = string.IsNullOrWhiteSpace(source.DisplayName) ? "выбранное окно" : source.DisplayName;
        Status($"Передача кадров разрешена: «{sourceName}». Снимаем первый кадр…");
        CaptureStatus.Text = "Снимаем кадр";
        CaptureLight.Fill = Active;
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

        await OnUiAsync(() =>
        {
            CaptureStatus.Text = "Снимаем кадр";
            CaptureLight.Fill = Active;
            Status("Снимаем кадр выбранного окна…");
        });
        var frame = await CaptureAsync(session.Source, token);
        if (frame.Fingerprint == _lastAnalyzedFrame)
        {
            await OnUiAsync(() => Status("Кадр не изменился. Ждём следующий снимок."));
            return null;
        }

        var waitingForFirst = _answers == 0;
        await OnUiAsync(() =>
        {
            _requests++;
            UpdateStats();
            CaptureStatus.Text = "Отправка";
            CaptureLight.Fill = Active;
            Status(waitingForFirst
                ? $"Кадр {frame.Width} × {frame.Height} отправлен. Ждём первый ответ…"
                : $"Кадр {frame.Width} × {frame.Height} отправлен. Ждём ответ…");
        });

        try
        {
            await ShowAnalysisAsync(session.Model,
                ct => session.Analyze(frame.Payload, ct),
                frame.Fingerprint,
                waitingForFirst
                    ? "Первый ответ получен. Анализ продолжается."
                    : "Ответ готов. Анализ продолжается.", token);
            await OnUiAsync(() =>
            {
                CaptureStatus.Text = "Ответ получен";
                CaptureLight.Fill = Active;
            });
            return null;
        }
        catch (AiException ex)
        {
            await OnUiAsync(() =>
            {
                AnswerTab.IsSelected = true;
                AnswerText.Text = ex.Message;
                CaptureStatus.Text = "Ошибка отправки";
                CaptureLight.Fill = Error;
                Status(ex.Message, error: true);
            });
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
            await OnUiAsync(() =>
            {
                AnswerTab.IsSelected = true;
                AnswerText.Text = "Оверлей закрыт. Включите плавающий ответ.";
                Status(AnswerText.Text, error: true);
            });
            throw new AiException("Оверлей закрыт. Включите плавающий ответ.");
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
                AddAnswer(model, result);
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
        if (license is null || !BuildConfiguration.TryGetLicenseServer(out var server) || server is null)
        {
            ShowFromTray();
            Status("Сначала введите код активации.", error: true);
            ShowActivation_Click(this, new RoutedEventArgs());
            return;
        }
        if (!ConfirmCaptureDestination(source, server))
        {
            Status("Отправка кадров отменена. Источник по-прежнему выбран.");
            return;
        }

        var model = "Сервер лицензий";
        Func<IReadOnlyList<ImagePayload>, CancellationToken, Task<string>> analyze = (frames, token) =>
            RunLicensedAnalysisAsync(license, key =>
                _activation.AnalyzeSeriesAsync(server, license.LicenseId, Guid.NewGuid(), key, frames, token));

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
            CapturedImage image;
            if (_periodicCapture is { } capture)
                image = await capture.TakeAsync(null, token);
            else
                image = await GraphicsCapture.TakeOnceAsync(source, null, token, _borderlessCapture);

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
            AnswerTab.IsSelected = true;
            AnswerText.Text = message;
            CaptureStatus.Text = "Ошибка";
            CaptureLight.Fill = Error;
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
            SourceText.Text = "Источник закрыт. Выберите окно ещё раз.";
            NextStepText.Text = "Нажмите «Выбрать…», укажите окно Chrome и подтвердите ОК.";
            NextStepText.FontWeight = FontWeights.Normal;
            NextStepText.Foreground = (Brush)FindResource("Muted");
            StartCue.Visibility = Visibility.Collapsed;
            Status("Доступ к источнику захвата потерян.", error: true);
            UpdateControls();
        });

    private void ClearPreview()
    {
        PreviewImage.Source = null;
        PreviewEmpty.Visibility = Visibility.Visible;
        FrameDetails.Text = "";
    }

    private bool ConfirmCaptureDestination(GraphicsCaptureItem source, Uri destination)
    {
        if (_confirmingCapture || _closing || _shutdownComplete || !ReferenceEquals(_source, source))
            return false;

        var approval = destination.AbsoluteUri;
        if (_approvedCaptureDestination == approval)
            return true;

        _confirmingCapture = true;
        try
        {
            var name = string.IsNullOrWhiteSpace(source.DisplayName) ? "выбранное окно" : source.DisplayName;
            var message = $"Источник · {name}\n"
                + $"Получатель · сервер подписки ({destination.Host})\n\n"
                + "Будет отправлено всё содержимое этого окна. "
                + "Остановка в приложении прекращает отправку новых кадров.\n\n"
                + "Разрешить передачу кадров?";
            ShowFromTray();
            using var systemDialog = OpenSystemDialog();
            Activate();
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

    private void UpdateControls()
    {
        var idle = _operation is null;
        var periodicIdle = _periodic is null && _scheduler is null;
        PickButton.IsEnabled = idle && periodicIdle;
        ActivationPanel.IsEnabled = idle && !_activating;
        PeriodicInterval.IsEnabled = idle;
        StartPeriodicButton.IsEnabled = idle && periodicIdle && _source is not null;
        if (_source is null)
            StartCue.Visibility = Visibility.Collapsed;
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
