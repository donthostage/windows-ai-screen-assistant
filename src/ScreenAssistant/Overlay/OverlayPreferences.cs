using System.IO;
using System.Text.Json;
using System.Windows;
using ScreenAssistant.Configuration;

namespace ScreenAssistant.Overlay;

internal sealed record OverlayPreferences
{
    public bool Enabled { get; init; }
    public bool Compact { get; init; } = true;
    public bool ExcludeFromCapture { get; init; } = true;
    public bool SolidBackground { get; init; }
    public double CompactHeight { get; init; } = 38;
    public double Width { get; init; } = DefaultWidth();
    public double Height { get; init; } = 180;
    public double FontSize { get; init; } = 16;
    public double PanelOpacity { get; init; } = .94;
    public int? X { get; init; }
    public int? Y { get; init; }

    private static string PathName => Path.Combine(LocalFiles.DirectoryPath, "overlay.json");

    private static double DefaultWidth()
    {
        try
        {
            return Math.Clamp(SystemParameters.WorkArea.Width * 0.6, 240, 1000);
        }
        catch
        {
            return 360;
        }
    }

    public static OverlayPreferences Load()
    {
        try
        {
            if (File.Exists(PathName)
                && JsonSerializer.Deserialize<OverlayPreferences>(File.ReadAllText(PathName)) is { } settings)
            {
                return settings with
                {
                    Width = Math.Clamp(settings.Width, 10, 2400),
                    CompactHeight = Math.Clamp(settings.CompactHeight, 10, 144),
                    Height = Math.Clamp(settings.Height, 10, 1400),
                    FontSize = Math.Clamp(settings.FontSize, 12, 26),
                    PanelOpacity = Math.Clamp(settings.PanelOpacity, 0, 1)
                };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LocalFiles.Log("overlay-load", ex);
        }

        return new();
    }

    public void Save() => LocalFiles.WriteAtomically(PathName,
        JsonSerializer.SerializeToUtf8Bytes(this, new JsonSerializerOptions { WriteIndented = true }));
}
