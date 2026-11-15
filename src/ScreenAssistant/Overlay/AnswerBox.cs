using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace ScreenAssistant.Overlay;

internal sealed class AnswerBox : TextBox
{
    private static readonly Brush SelectionFill = Freeze(Color.FromArgb(0xAA, 0x4D, 0xA3, 0xFF));
    private static readonly ControlTemplate BareTemplate = CreateTemplate();

    public static readonly DependencyProperty SourceTextProperty = DependencyProperty.Register(
        nameof(SourceText),
        typeof(string),
        typeof(AnswerBox),
        new FrameworkPropertyMetadata("", OnSourceChanged));

    private string _source = "";
    private bool _applying;

    public AnswerBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        AcceptsReturn = true;
        AcceptsTab = false;
        AutoWordSelection = false;
        UndoLimit = 0;
        AllowDrop = false;
        TextWrapping = TextWrapping.Wrap;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        HorizontalContentAlignment = HorizontalAlignment.Left;
        VerticalContentAlignment = VerticalAlignment.Top;
        BorderThickness = new Thickness(0);
        Background = Brushes.Transparent;
        Padding = new Thickness(0, 2, 6, 2);
        CaretBrush = Brushes.Transparent;
        SelectionBrush = SelectionFill;
        SelectionOpacity = 1;
        IsInactiveSelectionHighlightEnabled = true;
        IsTabStop = false;
        FocusVisualStyle = null;
        Cursor = Cursors.IBeam;
        Template = BareTemplate;
        SpellCheck.SetIsEnabled(this, false);
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, CopyPlain, CanCopy));
        Loaded += (_, _) => Reflow(resetScroll: false);
    }

    public string SourceText
    {
        get => (string)GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    internal void ScrollPage(bool down)
    {
        if (!IsVisible || !IsLoaded)
            return;

        if (down)
            PageDown();
        else
            PageUp();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var notches = e.Delta / 120;
        if (notches == 0)
            notches = e.Delta > 0 ? 1 : -1;

        var lines = SystemParameters.WheelScrollLines;
        if (lines < 0)
        {
            if (notches > 0)
                PageUp();
            else
                PageDown();
        }
        else
        {
            var steps = Math.Max(1, lines) * Math.Abs(notches);
            for (var i = 0; i < steps; i++)
            {
                if (notches > 0)
                    LineUp();
                else
                    LineDown();
            }
        }

        e.Handled = true;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (sizeInfo.WidthChanged)
            Reflow(resetScroll: false);
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == FontSizeProperty || e.Property == FontFamilyProperty || e.Property == PaddingProperty
            || e.Property == FontWeightProperty || e.Property == FontStyleProperty || e.Property == FontStretchProperty)
            Reflow(resetScroll: false);
    }

    private static void OnSourceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var box = (AnswerBox)sender;
        box._source = e.NewValue as string ?? "";
        box.Reflow(resetScroll: true);
    }

    private void Reflow(bool resetScroll)
    {
        if (_applying)
            return;

        var width = ContentWidth();
        var wrapped = width <= 1 || FontSize <= 0 || double.IsNaN(FontSize)
            ? _source
            : AnswerLineBreaker.Apply(_source, line => Fits(line, width));
        if (!resetScroll && Text == wrapped)
            return;

        var offset = resetScroll ? 0 : VerticalOffset;
        _applying = true;
        Text = wrapped;
        _applying = false;
        ScrollToVerticalOffset(offset);
    }

    private double ContentWidth()
    {
        var inner = ActualWidth - Padding.Left - Padding.Right;
        return inner > 8 ? inner - 3 : inner;
    }

    private bool Fits(string line, double width)
    {
        if (line.Length == 0)
            return true;

        var dpi = 1d;
        if (PresentationSource.FromVisual(this) is not null)
            dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (dpi <= 0)
            dpi = 1;

        var formatted = new FormattedText(
            line,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyle, FontWeight, FontStretch),
            FontSize,
            Brushes.Transparent,
            dpi);
        return formatted.WidthIncludingTrailingWhitespace <= width;
    }

    private void CanCopy(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = AnswerLineBreaker.Strip(SelectedText).Length > 0;
        e.Handled = true;
    }

    private void CopyPlain(object sender, ExecutedRoutedEventArgs e)
    {
        var plain = AnswerLineBreaker.Strip(SelectedText);
        e.Handled = true;
        if (plain.Length == 0)
            return;

        try
        {
            Clipboard.SetText(plain);
        }
        catch (ExternalException)
        {
            // Another process holds the clipboard. The selection stays put.
        }
    }

#pragma warning disable CS0618
    private static ControlTemplate CreateTemplate()
    {
        var presenter = new FrameworkElementFactory(typeof(ScrollContentPresenter));
        presenter.Name = "PART_ScrollContentPresenter";
        presenter.SetBinding(ContentPresenter.ContentProperty, ParentBinding(ContentPresenter.ContentProperty));
        presenter.SetValue(ScrollViewer.CanContentScrollProperty, true);

        var scrollTemplate = new ControlTemplate(typeof(ScrollViewer)) { VisualTree = presenter };
        var host = new FrameworkElementFactory(typeof(ScrollViewer));
        host.Name = "PART_ContentHost";
        host.SetValue(FocusableProperty, false);
        host.SetValue(ScrollViewer.CanContentScrollProperty, true);
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(TemplateProperty, scrollTemplate);
        host.SetBinding(BackgroundProperty, ParentBinding(BackgroundProperty));
        host.SetBinding(MarginProperty, ParentBinding(PaddingProperty));
        return new ControlTemplate(typeof(AnswerBox)) { VisualTree = host };
    }
#pragma warning restore CS0618

    private static Binding ParentBinding(DependencyProperty property) => new(property.Name)
    {
        RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
    };

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
