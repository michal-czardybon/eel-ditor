using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using CarbonPad.App.Services;
using CarbonPad.App.Views;

namespace CarbonPad.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        StartupTrace.Mark("framework initialized");
        _ = CarbonPad.Core.Git.GitService.AvailableAsync(); // probe git in the background, never on the UI thread
        var settings = SettingsStore.Load();
        Loc.Load(settings.Language);
        ApplyTheme(settings.Theme);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // A bug in one command must not take the whole editor (and unsaved work) down with it.
            Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                ErrorLog.Write(e.Exception);
                e.Handled = true;
                (desktop.MainWindow as MainWindow)?.ReportError(e.Exception);
            };
            TaskScheduler.UnobservedTaskException += (_, e) => { ErrorLog.Write(e.Exception); e.SetObserved(); };
            AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) ErrorLog.Write(ex); };
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
            StartupTrace.Mark("main window: create");
            var window = new MainWindow(settings, Program.StartupArgs);
            StartupTrace.Mark("main window: created");
            desktop.MainWindow = window;
            var stop = new CancellationTokenSource();
            desktop.Exit += (_, _) => stop.Cancel();
            SingleInstance.Listen(files => Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = window.OpenFromOtherInstanceAsync(files)), stop.Token);
        }
        base.OnFrameworkInitializationCompleted();
    }

    public static void ApplyTheme(AppTheme theme)
    {
        if (Current is null) return;
        Current.RequestedThemeVariant = theme switch
        {
            AppTheme.Dark => ThemeVariant.Dark,
            AppTheme.Light => ThemeVariant.Light,
            _ => ThemeVariant.Default,
        };
    }
}
