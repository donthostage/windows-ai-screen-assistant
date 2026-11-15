using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using ScreenAssistant.Core.Overlay;

namespace ScreenAssistant.Overlay;

internal sealed class OverlayViewModel(OverlayPreferences preferences) : INotifyPropertyChanged
{
    private OverlayState _state = new("Готово к тренировке", "", OverlayPhase.Idle);
    private OverlayPreferences _preferences = preferences;

    public string Status => _state.Status;
    public string Text => _state.Text;
    public bool HasError => _state.Phase == OverlayPhase.Error;
    public string CompactText => HasError || _state.Phase == OverlayPhase.Loading || string.IsNullOrWhiteSpace(Text)
        ? Status : string.Join(" · ", Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    public double DisplayHeight => Compact ? CompactHeight : Height;
    public bool UsesHorizontalLayout => Compact || DisplayHeight <= 64;
    public double CompactFontSize => Math.Clamp(DisplayHeight - 14, 1, FontSize);

    public bool Compact
    {
        get => _preferences.Compact;
        set
        {
            SetAppearance(_preferences with { Compact = value });
            Changed(nameof(DisplayHeight));
            Changed(nameof(UsesHorizontalLayout));
            Changed(nameof(CompactFontSize));
        }
    }

    public bool ExcludeFromCapture
    {
        get => _preferences.ExcludeFromCapture;
        set => SetAppearance(_preferences with { ExcludeFromCapture = value });
    }

    public bool SolidBackground
    {
        get => _preferences.SolidBackground;
        set => SetAppearance(_preferences with { SolidBackground = value });
    }

    public double CompactHeight
    {
        get => _preferences.CompactHeight;
        set
        {
            SetAppearance(_preferences with { CompactHeight = value });
            Changed(nameof(DisplayHeight));
            Changed(nameof(UsesHorizontalLayout));
            Changed(nameof(CompactFontSize));
        }
    }

    public double Width
    {
        get => _preferences.Width;
        set => SetAppearance(_preferences with { Width = value });
    }

    public double Height
    {
        get => _preferences.Height;
        set
        {
            SetAppearance(_preferences with { Height = value });
            Changed(nameof(DisplayHeight));
            Changed(nameof(UsesHorizontalLayout));
            Changed(nameof(CompactFontSize));
        }
    }

    public double FontSize
    {
        get => _preferences.FontSize;
        set
        {
            SetAppearance(_preferences with { FontSize = value });
            Changed(nameof(CompactFontSize));
        }
    }

    public Thickness TextInset => new(PanelOpacity < .85 ? 24 : 0, 0, 0, 0);

    public Brush HeaderBackground
    {
        get
        {
            const double minimumOpacity = .65;
            if (PanelOpacity == 0 || PanelOpacity >= minimumOpacity)
                return Brushes.Transparent;

            // The header is layered over the panel fill. Keep their combined opacity at 65%.
            var alpha = (byte)Math.Ceiling(255 * (minimumOpacity - PanelOpacity) / (1 - PanelOpacity));
            var brush = new SolidColorBrush(Color.FromArgb(alpha, 0x28, 0x28, 0x28));
            brush.Freeze();
            return brush;
        }
    }

    public double PanelOpacity
    {
        get => _preferences.PanelOpacity;
        set
        {
            SetAppearance(_preferences with { PanelOpacity = value });
            Changed(nameof(HeaderBackground));
            Changed(nameof(TextInset));
        }
    }

    public OverlayPreferences Preferences => _preferences;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? PreferencesChanged;

    public void Render(OverlayState state)
    {
        if (_state == state)
            return;

        var previous = _state;
        _state = state;
        if (previous.Status != state.Status)
            Changed(nameof(Status));
        if (previous.Text != state.Text)
            Changed(nameof(Text));
        if (previous.Phase != state.Phase)
            Changed(nameof(HasError));
        Changed(nameof(CompactText));
    }

    public void SetEnabled(bool enabled) =>
        SetPreferences(_preferences with { Enabled = enabled });

    public void SetPosition(int x, int y) =>
        SetPreferences(_preferences with { X = x, Y = y });

    public void ResetPosition()
    {
        SetPreferences(_preferences with { X = null, Y = null });
        Changed(nameof(Preferences));
    }

    private void SetAppearance(OverlayPreferences value, [CallerMemberName] string? name = null)
    {
        if (SetPreferences(value))
            Changed(name);
    }

    private bool SetPreferences(OverlayPreferences value)
    {
        if (_preferences == value)
            return false;

        _preferences = value;
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void Changed(string? name) => PropertyChanged?.Invoke(this, new(name));
}
