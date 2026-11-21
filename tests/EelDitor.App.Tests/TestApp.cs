using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(EelDitor.App.Tests.TestApp))]

namespace EelDitor.App.Tests;

public sealed class TestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://EelDitor.App.Tests")) { Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml") });
        Styles.Add(new StyleInclude(new Uri("avares://EelDitor.App.Tests")) { Source = new Uri("avares://EelDitor/Views/Styles.axaml") });
        EelDitor.App.Services.Loc.Load("pl");
        EelDitor.App.Editor.MarkdownParagraphs.Register();
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        // Never touch the user's settings and session in %APPDATA%.
        string dir = Path.Combine(Path.GetTempPath(), "EelDitor.Tests", Environment.ProcessId.ToString());
        Environment.SetEnvironmentVariable("EELDITOR_CONFIG_DIR", dir);
        return AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
    }
}

/// <summary>A fresh, empty settings folder, so a window does not restore files left by another test.</summary>
public static class TestDirs
{
    public static string New() => Path.Combine(Path.GetTempPath(), "EelDitor.Tests", Guid.NewGuid().ToString("N"));
}
