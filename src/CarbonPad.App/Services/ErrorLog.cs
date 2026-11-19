namespace CarbonPad.App.Services;

/// <summary>Appends unexpected exceptions to errors.log in the settings directory.</summary>
public static class ErrorLog
{
    public static string FilePath => Path.Combine(SettingsStore.Directory, "errors.log");

    public static void Write(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.Directory);
            var info = new FileInfo(FilePath);
            if (info.Exists && info.Length > 2 * 1024 * 1024) info.Delete();
            File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException)
        {
            // Logging is best effort.
        }
    }
}
