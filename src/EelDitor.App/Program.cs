using System.Runtime;
using Avalonia;

namespace EelDitor.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Low-latency GC keeps typing smooth while the machine is busy (e.g. compiling).
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        Services.StartupTrace.Mark("Main");
        // Already running: hand the files to that window and exit.
        if (Services.SingleInstance.TryHandOver(args)) return;
        Services.StartupTrace.Mark("single instance checked");
        Services.NativeSplash.Show(Services.AppInfo.Name);
        Services.StartupTrace.Mark("splash");
        Services.Warmup.Start();
        Services.Loc.Load(Services.SettingsStore.Load().Language);
        Services.NativeSplash.SetSubtitle(Services.Loc.T("About.Slogan"));
        Services.StartupTrace.Mark("language");
        StartupArgs = args;
        Services.StartupTrace.Mark("Avalonia start");
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static string[] StartupArgs { get; private set; } = [];

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
