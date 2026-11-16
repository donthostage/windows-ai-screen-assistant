using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using ScreenAssistant.Configuration;
using ScreenAssistant.Core.AIClient;
using ScreenAssistant.Core.Overlay;
using ScreenAssistant.Overlay;

namespace ScreenAssistant;

public partial class App : Application
{
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan,
        MaxResponseContentBufferSize = 32 * 1024 * 1024
    };

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            LocalFiles.Log("dispatcher", args.Exception);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LocalFiles.Log("unobserved-task", args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                LocalFiles.Log("appdomain", ex);
        };

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
        _http.Dispose();
        base.OnExit(e);
    }
}
