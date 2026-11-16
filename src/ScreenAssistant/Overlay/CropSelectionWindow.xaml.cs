using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Interop;

namespace ScreenAssistant.Overlay;

public partial class CropSelectionWindow : Window
{
    private const int GwlExstyle = -20;
    private const int WsExAppWindow = 0x00040000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    private Point _start;
    private bool _dragging;
    private nint _hwnd;

    public Int32Rect SelectedPixels { get; private set; }
    public nint SelectedMonitor { get; private set; }

    public CropSelectionWindow()
    {
        InitializeComponent();
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(_hwnd, GwlExstyle);
        style = (style | WsExToolWindow | WsExNoActivate) & ~WsExAppWindow;
        SetWindowLong(_hwnd, GwlExstyle, style);
        CaptureExclusion.Apply(_hwnd, true);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
        }
        else if (e.Key == Key.Enter)
            FinishSelection();
    }

    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _start = e.GetPosition(Surface);
        Canvas.SetLeft(Selection, _start.X);
        Canvas.SetTop(Selection, _start.Y);
        Selection.Width = 0;
        Selection.Height = 0;
        Selection.Visibility = Visibility.Visible;
        HintText.Text = "Выделите область. Esc — отмена.";
        Surface.CaptureMouse();
    }

    private void Surface_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
            return;

        var current = e.GetPosition(Surface);
        var x = Math.Min(_start.X, current.X);
        var y = Math.Min(_start.Y, current.Y);
        var w = Math.Abs(current.X - _start.X);
        var h = Math.Abs(current.Y - _start.Y);
        Canvas.SetLeft(Selection, x);
        Canvas.SetTop(Selection, y);
        Selection.Width = w;
        Selection.Height = h;
    }

    private void Surface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
            return;

        _dragging = false;
        Surface.ReleaseMouseCapture();
        FinishSelection();
    }

    private void FinishSelection()
    {
        var xDip = Canvas.GetLeft(Selection);
        var yDip = Canvas.GetTop(Selection);
        var wDip = Selection.Width;
        var hDip = Selection.Height;
        if (double.IsNaN(xDip) || double.IsNaN(yDip) || wDip < 40 || hDip < 40)
        {
            HintText.Text = "Слишком маленькая область";
            return;
        }

        if (!TryMapToPixels(xDip, yDip, wDip, hDip, out var left, out var top, out var width, out var height))
        {
            var topLeft = Surface.PointToScreen(new Point(xDip, yDip));
            var bottomRight = Surface.PointToScreen(new Point(xDip + wDip, yDip + hDip));
            left = (int)Math.Round(topLeft.X);
            top = (int)Math.Round(topLeft.Y);
            width = (int)Math.Round(bottomRight.X) - left;
            height = (int)Math.Round(bottomRight.Y) - top;
        }

        SelectedPixels = new Int32Rect(left, top, width, height);
        SelectedMonitor = MonitorFromRect(new RectPixels
        {
            Left = left,
            Top = top,
            Right = left + width,
            Bottom = top + height
        }, 0);
        if (SelectedMonitor == 0)
        {
            SelectedMonitor = MonitorFromPoint(new PointPixels(left + width / 2, top + height / 2), 0);
        }

        DialogResult = true;
        Close();
    }

    private bool TryMapToPixels(double xDip, double yDip, double wDip, double hDip,
        out int left, out int top, out int width, out int height)
    {
        left = top = width = height = 0;
        var probe = Surface.PointToScreen(new Point(xDip + wDip / 2, yDip + hDip / 2));
        var monitor = MonitorFromPoint(new PointPixels((int)Math.Round(probe.X), (int)Math.Round(probe.Y)), 0);
        if (monitor == 0)
            return false;

        var hr = GetDpiForMonitor(monitor, 0, out var dpiX, out var dpiY);
        if (hr != 0 || dpiX == 0 || dpiY == 0)
            return false;

        var origin = new Point(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop);
        var originScreen = PointToScreen(origin);
        left = (int)Math.Round(originScreen.X + xDip * dpiX / 96.0);
        top = (int)Math.Round(originScreen.Y + yDip * dpiY / 96.0);
        width = (int)Math.Round(wDip * dpiX / 96.0);
        height = (int)Math.Round(hDip * dpiY / 96.0);
        return width > 0 && height > 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectPixels
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PointPixels(int X, int Y);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint MonitorFromRect(ref RectPixels rect, uint flags);

    private static nint MonitorFromRect(RectPixels rect, uint flags) => MonitorFromRect(ref rect, flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint MonitorFromPoint(PointPixels point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(nint hwnd, int index, int value);
}
