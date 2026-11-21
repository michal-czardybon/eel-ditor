using TextMateSharp.Grammars;

namespace EelDitor.App.Services;

/// <summary>
/// Work started at the very beginning of Main on a background thread, while Avalonia initializes on the
/// UI thread: the code page provider and the TextMate theme/grammar registry (JSON parsing). Only objects
/// that do not belong to the UI are prepared here.
/// </summary>
internal static class Warmup
{
    private static Task<RegistryOptions>? _registry;

    public static void Start()
    {
        _registry ??= Task.Run(() =>
        {
            EelDitor.Core.Text.TextEncoding.EnsureInitialized();
            var options = new RegistryOptions(ThemeName.DarkPlus);
            options.LoadTheme(ThemeName.DarkPlus);
            options.GetScopeByLanguageId("csharp"); // reads the grammar index
            StartupTrace.Mark("warm-up done (background)");
            return options;
        });
    }

    /// <summary>The registry prepared in the background (waits for it if it is not ready yet).</summary>
    public static RegistryOptions TextMateRegistry()
    {
        Start();
        return _registry!.GetAwaiter().GetResult();
    }
}
