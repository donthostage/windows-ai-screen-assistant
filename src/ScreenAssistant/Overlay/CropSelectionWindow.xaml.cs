using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;

namespace ScreenAssistant.Overlay;

public partial class CropSelectionWindow : Window
{
    private Point _start;
    private bool _dragging;

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

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;

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
            DialogResult = false;

            return;
        }

        var topLeft = Surface.PointToScreen(new Point(xDip, yDip));
        var bottomRight = Surface.PointToScreen(new Point(xDip + wDip, yDip + hDip));
        var left = (int)Math.Round(topLeft.X);
        var top = (int)Math.Round(topLeft.Y);
        var width = (int)Math.Round(bottomRight.X) - left;
        var height = (int)Math.Round(bottomRight.Y) - top;
        SelectedPixels = new Int32Rect(left, top, width, height);
        SelectedMonitor = MonitorFromRect(new RectPixels
        {
            Left = left,
            Top = top,
            Right = left + width,
            Bottom = top + height
        }, 0);
        DialogResult = true;

    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectPixels
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint MonitorFromRect(ref RectPixels rect, uint flags);

    private static nint MonitorFromRect(RectPixels rect, uint flags) => MonitorFromRect(ref rect, flags);
}
