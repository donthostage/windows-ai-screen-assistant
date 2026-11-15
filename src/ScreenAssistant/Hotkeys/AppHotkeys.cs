namespace ScreenAssistant.Hotkeys;

internal static class AppHotkeys
{
    public const int Overlay = 2;
    public const int ShowWindow = 6;
    public const int Scroll = 7;
    public const int AnswerPageUp = 8;
    public const int AnswerPageDown = 9;

    public static readonly (int Id, uint Modifiers, uint Vk, string Label)[] Defaults =
    [
        (Overlay, HotkeyManager.ModControl | HotkeyManager.ModAlt, 0x48, "Ctrl+Alt+H"),
        (ShowWindow, HotkeyManager.ModControl | HotkeyManager.ModAlt, 0x4D, "Ctrl+Alt+M"),
        (Scroll, HotkeyManager.ModControl | HotkeyManager.ModAlt, 0x4C, "Ctrl+Alt+L"),
        (AnswerPageUp, HotkeyManager.ModControl | HotkeyManager.ModAlt, 0x4B, "Ctrl+Alt+K"),
        (AnswerPageDown, HotkeyManager.ModControl | HotkeyManager.ModAlt, 0x4A, "Ctrl+Alt+J")
    ];
}
