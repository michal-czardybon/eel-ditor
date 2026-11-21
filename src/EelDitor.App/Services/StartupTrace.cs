using System.Diagnostics;

namespace EelDitor.App.Services;

/// <summary>Startup timing, written to the file named by EELDITOR_STARTUP_TRACE (off otherwise).</summary>
internal static class StartupTrace
{
    private static readonly string? Path = Environment.GetEnvironmentVariable("EELDITOR_STARTUP_TRACE");
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    public static void Mark(string what)
    {
        if (Path is null) return;
        var since = DateTime.Now - Process.GetCurrentProcess().StartTime;
        try { File.AppendAllText(Path, $"{since.TotalMilliseconds,7:0} ms since process start  {what}{Environment.NewLine}"); }
        catch (IOException) { }
    }
}
