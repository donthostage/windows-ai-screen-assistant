using System.Net.Http;
using System.Windows;
using ScreenAssistant.Core.AIClient;
using ScreenAssistant.Core.Overlay;
using ScreenAssistant.Overlay;

namespace ScreenAssistant;

public partial class App : Application
{
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan,
        MaxResponseContentBufferSize = 16 * 1024 * 1024
    };

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var preferences = OverlayPreferences.Load();
        var renderer = new WpfOverlayRenderer(preferences, settings => settings.Save());
        var overlay = new OverlayManager(new WpfOverlayDispatcher(Dispatcher), renderer);
        var main = new MainWindow(new AiClient(_http), overlay,
            preferences.Enabled, renderer.SetEnabledPreference);
        renderer.Attach(main);
        renderer.CloseRequested += (_, _) => main.DisableOverlay();
        renderer.CaptureExclusionFailed += (_, _) => main.WarnOverlayCaptureInclusion();
        MainWindow = main;
        main.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // MainWindow awaits overlay shutdown and cancels in-flight work before Close completes.
        _http.Dispose();
        base.OnExit(e);
    }
}
