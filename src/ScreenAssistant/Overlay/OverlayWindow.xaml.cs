using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ScreenAssistant.Overlay;

public partial class OverlayWindow : Window
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint PositionOnly = 0x0001 | 0x0004 | 0x0010;
    private const int DisplayChanged = 0x007E;
    private const int SettingsChanged = 0x001A;
    private const int GwlExstyle = -20;
    private const int WsExAppWindow = 0x00040000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExTransparent = 0x00000020;
    private readonly OverlayViewModel _viewModel;
    private readonly Window? _mainWindow;
    private HwndSource? _source;
    private nint _hwnd;
    private bool _clickThrough;
    private readonly DispatcherTimer _answerAutoscroll = new() { Interval = TimeSpan.FromMilliseconds(40) };

    internal bool IsExcludedFromCapture { get; private set; }

    internal OverlayWindow(OverlayViewModel viewModel, Window? mainWindow)
    {
        _viewModel = viewModel;
        _mainWindow = mainWindow;
        if (viewModel.Preferences.SolidBackground)
        {
            AllowsTransparency = false;
            Background = new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x28));
        }
        else
        {
            AllowsTransparency = true;
            Background = Brushes.Transparent;
        }

        InitializeComponent();
        _answerAutoscroll.Tick += (_, _) => AdvanceAnswer();
        DataContext = viewModel;
        Width = viewModel.Width;
        Height = viewModel.DisplayHeight;
        _viewModel.PropertyChanged += ViewModelChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WindowMessage);
        ApplyExtendedStyles();
        ApplyExclusion();
        Place(_viewModel.Preferences.X, _viewModel.Preferences.Y);
        Dispatcher.InvokeAsync(() => Place(_viewModel.Preferences.X, _viewModel.Preferences.Y),
            DispatcherPriority.Loaded);
    }

    internal void SetClickThrough(bool enabled)
    {
        if (_clickThrough == enabled)
            return;

        _clickThrough = enabled;
        if (_hwnd != 0)
            ApplyExtendedStyles();
    }

    internal void ApplyExclusion()
    {
        if (_hwnd == 0)
            return;

        if (_viewModel.Preferences.ExcludeFromCapture)
            IsExcludedFromCapture = CaptureExclusion.Apply(_hwnd, true);
        else
        {
            CaptureExclusion.Apply(_hwnd, false);
            IsExcludedFromCapture = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _answerAutoscroll.Stop();
        _viewModel.PropertyChanged -= ViewModelChanged;
        _source?.RemoveHook(WindowMessage);
        _source = null;
        _hwnd = 0;
        base.OnClosed(e);
    }

    private void CompactAnswerUpdated(object sender, System.Windows.Data.DataTransferEventArgs e)
    {
        _answerAutoscroll.Stop();
        CompactScroll.ScrollToHorizontalOffset(0);
    }

    internal void ToggleAnswerAutoscroll()
    {
        if (_answerAutoscroll.IsEnabled)
            _answerAutoscroll.Stop();
        else if (IsVisible && !string.IsNullOrWhiteSpace(_viewModel.Text))
            _answerAutoscroll.Start();
    }

    private void AdvanceAnswer()
    {
        var horizontal = CompactScroll.IsVisible;
        var viewer = horizontal ? CompactScroll : Answer;
        var offset = horizontal ? viewer.HorizontalOffset : viewer.VerticalOffset;
        var limit = horizontal ? viewer.ScrollableWidth : viewer.ScrollableHeight;
        if (!viewer.IsVisible || offset >= limit - 0.5)
        {
            _answerAutoscroll.Stop();
            return;
        }

        if (horizontal)
            viewer.ScrollToHorizontalOffset(Math.Min(offset + 2.4, limit));
        else
            viewer.ScrollToVerticalOffset(Math.Min(offset + 1.2, limit));
    }

    internal void ScrollAnswer(bool down)
    {
        _answerAutoscroll.Stop();
        if (CompactScroll.IsVisible)
        {
            var direction = down ? 1 : -1;
            var offset = CompactScroll.HorizontalOffset + direction * CompactScroll.ViewportWidth * 0.85;
            CompactScroll.ScrollToHorizontalOffset(Math.Clamp(offset, 0, CompactScroll.ScrollableWidth));
            return;
        }

        if (!Answer.IsVisible)
            return;

        if (down)
            Answer.PageDown();
        else
            Answer.PageUp();
    }

    private void DragHeader(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        DragMove();
        if (GetWindowRect(_hwnd, out var rect))
        {
            Place(rect.Left, rect.Top, MonitorFromWindow(_hwnd, MonitorDefaultToNearest));
            if (GetWindowRect(_hwnd, out var placed))
                _viewModel.SetPosition(placed.Left, placed.Top);
        }
    }

    private nint WindowMessage(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg is DisplayChanged or SettingsChanged)
        {
            Dispatcher.InvokeAsync(() =>
            {
                ApplyExclusion();
                KeepOnScreen();
            }, DispatcherPriority.Loaded);
        }

        return 0;
    }

    private void ViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OverlayViewModel.Width) or nameof(OverlayViewModel.DisplayHeight))
            KeepOnScreen();
        else if (e.PropertyName == nameof(OverlayViewModel.Preferences))
            Place(null, null);
        else if (e.PropertyName == nameof(OverlayViewModel.ExcludeFromCapture))
            ApplyExclusion();
    }

    private void KeepOnScreen()
    {
        if (_hwnd != 0 && GetWindowRect(_hwnd, out var rect))
            Place(rect.Left, rect.Top);
    }

    private void Place(int? x, int? y, nint preferredMonitor = 0)
    {
        if (_hwnd == 0)
            return;

        var monitor = preferredMonitor;
        if (monitor == 0 && x is int left && y is int top)
            monitor = MonitorFromPoint(new PointPixels(left, top), 0);
        var restore = monitor != 0;
        if (!restore)
        {
            var owner = _mainWindow is null ? _hwnd : new WindowInteropHelper(_mainWindow).Handle;
            monitor = MonitorFromWindow(owner, MonitorDefaultToNearest);
        }

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info))
            return;

        var area = info.Work;
        var dpi = VisualTreeHelper.GetDpi(this);
        Width = Math.Min(_viewModel.Width, (area.Right - area.Left) / dpi.DpiScaleX);
        Height = Math.Min(_viewModel.DisplayHeight, (area.Bottom - area.Top) / dpi.DpiScaleY);
        var width = (int)Math.Ceiling(Width * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(Height * dpi.DpiScaleY);
        var maxX = Math.Max(area.Left, area.Right - width);
        var maxY = Math.Max(area.Top, area.Bottom - height);
        var targetX = restore ? x!.Value : area.Left + (area.Right - area.Left - width) / 2;
        var targetY = restore ? y!.Value : maxY - 40;
        SetWindowPos(_hwnd, 0, Math.Clamp(targetX, area.Left, maxX),
            Math.Clamp(targetY, area.Top, maxY), 0, 0, PositionOnly);
    }

    private void ApplyExtendedStyles()
    {
        if (_hwnd == 0)
            return;

        var style = GetWindowLong(_hwnd, GwlExstyle);
        style = (style | WsExToolWindow | WsExNoActivate) & ~WsExAppWindow;
        if (_clickThrough)
            style |= WsExTransparent;
        else
            style &= ~WsExTransparent;
        SetWindowLong(_hwnd, GwlExstyle, style);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PointPixels(int X, int Y);

    [StructLayout(LayoutKind.Sequential)]
    private struct RectPixels
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public RectPixels Monitor, Work;
        public uint Flags;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint MonitorFromPoint(PointPixels point, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool GetWindowRect(nint hwnd, out RectPixels rect);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width,
        int height, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(nint hwnd, int index, int value);
}
