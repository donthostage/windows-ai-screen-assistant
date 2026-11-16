using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using ScreenAssistant.Configuration;

namespace ScreenAssistant.Tray;

internal sealed class AppTrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public AppTrayIcon(Action showWindow, Action exitApp, Action stopAnalysis)
    {
        Icon? icon = null;
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
                icon = Icon.ExtractAssociatedIcon(path);
        }
        catch (Exception ex)
        {
            LocalFiles.Log("tray-icon", ex);
        }

        _icon = new NotifyIcon
        {
            Text = "AI Screen Assistant",
            Icon = icon ?? SystemIcons.Application,
            Visible = true
        };

        void Marshal(Action action)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
                action();
            else
                dispatcher.Invoke(action);
        }

        _icon.Click += (_, e) =>
        {
            if (e is MouseEventArgs { Button: MouseButtons.Left })
                Marshal(showWindow);
        };
        _icon.DoubleClick += (_, _) => Marshal(showWindow);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть окно", null, (_, _) => Marshal(showWindow));
        menu.Items.Add("Остановить анализ", null, (_, _) => Marshal(stopAnalysis));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => Marshal(exitApp));
        _icon.ContextMenuStrip = menu;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
