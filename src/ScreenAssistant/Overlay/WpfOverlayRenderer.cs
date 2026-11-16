using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using ScreenAssistant.Configuration;
using ScreenAssistant.Core.Overlay;

namespace ScreenAssistant.Overlay;

internal sealed class WpfOverlayDispatcher(Dispatcher dispatcher) : IOverlayDispatcher
{
    public Task InvokeAsync(Action action)
    {
        if (!dispatcher.CheckAccess())
            return dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task;

        action();
        return Task.CompletedTask;
    }
}

internal sealed class WpfOverlayRenderer : IOverlayRenderer
{
    private readonly OverlayViewModel _viewModel;
    private readonly Action<OverlayPreferences> _save;
    private readonly DispatcherTimer _saveTimer;
    private OverlayWindow? _window;
    private Window? _mainWindow;
    private bool _controlsInFront;
    private bool _dirty;
    private bool _solidBackground;
    private bool _clickThrough = true;
    private bool _exclusionRecoveryTried;
    private OverlayPreferences? _lastSaved;

    public WpfOverlayRenderer(OverlayPreferences preferences, Action<OverlayPreferences> save)
    {
        _viewModel = new OverlayViewModel(preferences);
        _solidBackground = preferences.SolidBackground;
        _lastSaved = preferences;
        _save = save;
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(800)
        };
        _saveTimer.Tick += (_, _) => Flush();
        _viewModel.PreferencesChanged += (_, _) =>
        {
            _dirty = true;
            _saveTimer.Stop();
            _saveTimer.Start();
            if (_viewModel.Preferences.SolidBackground != _solidBackground && _window is not null)
                RecreateVisibleWindow();
        };
    }

    public event EventHandler? CloseRequested;
    public event EventHandler? CaptureExclusionFailed;

    public bool IsExcludedFromCapture => _window is null || _window.IsExcludedFromCapture;

    public void Attach(MainWindow mainWindow)
    {
        _mainWindow = mainWindow;
        mainWindow.BindOverlaySettings(_viewModel);
        var app = Application.Current;
        var appActive = mainWindow.IsActive;
        app.Activated += (_, _) =>
        {
            appActive = true;
            UpdateControlsLayer();
        };
        app.Deactivated += (_, _) =>
        {
            appActive = false;
            UpdateControlsLayer();
        };
        mainWindow.IsVisibleChanged += (_, _) => UpdateControlsLayer();
        mainWindow.StateChanged += (_, _) => UpdateControlsLayer();

        void UpdateControlsLayer()
        {
            _controlsInFront = appActive && mainWindow.IsVisible
                && mainWindow.WindowState != WindowState.Minimized;
            ApplyWindowLayer();
        }

        mainWindow.AnswerAutoscrollToggled += () => _window?.ToggleAnswerAutoscroll();
        mainWindow.AnswerScrollRequested += down => _window?.ScrollAnswer(down);
    }

    private void ApplyWindowLayer()
    {
        if (_mainWindow is { } main)
            main.Topmost = _controlsInFront && _window is not null;
        if (_window is { } overlay)
            overlay.Topmost = !_controlsInFront;
    }

    public void SetEnabledPreference(bool enabled)
    {
        _viewModel.SetEnabled(enabled);
        Flush();
    }

    public void Render(OverlayState state) => _viewModel.Render(state);

    public void SetClickThrough(bool enabled)
    {
        _clickThrough = enabled;
        _window?.SetClickThrough(enabled);
    }

    public void RefreshCaptureExclusion() => _window?.ApplyExclusion();

    public void SetVisible(bool visible)
    {
        if (!visible)
        {
            CloseWindow();
            Flush();
            return;
        }

        if (_window is not null)
            return;

        OpenWindow();
    }

    private void RecreateVisibleWindow()
    {
        try
        {
            CloseWindow();
            OpenWindow();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            LocalFiles.Log("overlay-recreate", ex);
            if (_viewModel.Preferences.SolidBackground)
            {
                _viewModel.SolidBackground = false;
                try
                {
                    CloseWindow();
                    OpenWindow();
                }
                catch (Exception retry) when (retry is InvalidOperationException or Win32Exception)
                {
                    LocalFiles.Log("overlay-recreate-retry", retry);
                    throw;
                }
            }
            else
                throw;
        }
    }

    private void OpenWindow()
    {
        _solidBackground = _viewModel.Preferences.SolidBackground;
        var window = new OverlayWindow(_viewModel, _mainWindow);
        window.Closed += UserClosed;
        _window = window;
        try
        {
            ApplyWindowLayer();
            window.Show();
            if (_clickThrough)
                window.SetClickThrough(true);
            if (_viewModel.Preferences.ExcludeFromCapture && !window.IsExcludedFromCapture)
            {
                CaptureExclusionFailed?.Invoke(this, EventArgs.Empty);
                if (!_exclusionRecoveryTried)
                {
                    _exclusionRecoveryTried = true;
                    _viewModel.SolidBackground = true;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            window.Closed -= UserClosed;
            _window = null;
            ApplyWindowLayer();
            window.Close();
            throw;
        }
    }

    private void CloseWindow()
    {
        if (_window is not { } current)
            return;

        _window = null;
        ApplyWindowLayer();
        _exclusionRecoveryTried = false;
        current.Closed -= UserClosed;
        current.Close();
    }

    public void Flush()
    {
        _saveTimer.Stop();
        if (!_dirty)
            return;

        var next = _viewModel.Preferences;
        if (_lastSaved == next)
        {
            _dirty = false;
            return;
        }

        try
        {
            _save(next);
            _lastSaved = next;
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LocalFiles.Log("overlay-save", ex);
        }
    }

    private void UserClosed(object? sender, EventArgs e)
    {
        if (sender is OverlayWindow window)
            window.Closed -= UserClosed;

        _window = null;
        ApplyWindowLayer();
        _exclusionRecoveryTried = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
