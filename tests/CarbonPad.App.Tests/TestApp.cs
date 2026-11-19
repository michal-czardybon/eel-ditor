using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(CarbonPad.App.Tests.TestApp))]

namespace CarbonPad.App.Tests;

public sealed class TestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://CarbonPad.App.Tests")) { Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml") });
        Styles.Add(new StyleInclude(new Uri("avares://CarbonPad.App.Tests")) { Source = new Uri("avares://CarbonEditor/Views/Styles.axaml") });
        CarbonPad.App.Services.Loc.Load("pl");
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        // Never touch the user's settings and session in %APPDATA%.
        string dir = Path.Combine(Path.GetTempPath(), "CarbonPad.Tests", Environment.ProcessId.ToString());
        Environment.SetEnvironmentVariable("CARBONPAD_CONFIG_DIR", dir);
        return AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
    }
}

/// <summary>A fresh, empty settings folder, so a window does not restore files left by another test.</summary>
public static class TestDirs
{
    public static string New() => Path.Combine(Path.GetTempPath(), "CarbonPad.Tests", Guid.NewGuid().ToString("N"));
}
