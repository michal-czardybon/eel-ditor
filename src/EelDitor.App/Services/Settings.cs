using System.Text.Json;
using System.Text.Json.Serialization;

namespace EelDitor.App.Services;

public enum AppTheme { Dark, System, Light }

public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public string Language { get; set; } = Loc.SystemDefault;
    /// <summary>The default: monospace fonts with a real bold face (Consolas on Windows).</summary>
    public const string DefaultFontFamily = "Consolas, Menlo, DejaVu Sans Mono, monospace";

    public string FontFamily { get; set; } = DefaultFontFamily;

    /// <summary>Set once Cascadia (a variable font whose bold is not drawn) has been replaced by the default.</summary>
    public bool BoldFontChecked { get; set; }
    public double FontSize { get; set; } = 13;
    public int TabSize { get; set; } = 4;
    public bool UseSpaces { get; set; } = true;
    public bool WordWrap { get; set; }
    public bool ShowSpacesAndTabs { get; set; }
    public bool ShowEndOfLine { get; set; }
    public bool ShowLineNumbers { get; set; } = true;
    public bool HighlightCurrentLine { get; set; } = true;
    public bool AutoReloadUnmodified { get; set; } = true;
    public bool MarkdownRaw { get; set; }
    public bool DiffSyncScroll { get; set; } = true;

    /// <summary>Height of the comparison's bottom line panel set by dragging; null = automatic.</summary>
    public double? DiffLinePanelHeight { get; set; }

    /// <summary>How new paragraphs are broken into lines while typing (Markdown and plain text).</summary>
    public EelDitor.Core.Text.WrapMode ParagraphWrap { get; set; } = EelDitor.Core.Text.WrapMode.OneLine;

    /// <summary>Compare in word-diff mode (git diff --word-diff) instead of side by side.</summary>
    public bool DiffWordMode { get; set; }

    /// <summary>Above this size syntax highlighting, Markdown rendering and word wrap are off by default.</summary>
    public int LargeFileThresholdMb { get; set; } = 10;

    public double SidebarWidth { get; set; } = 260;
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 820;
    public double? WindowX { get; set; }
    public double? WindowY { get; set; }
    public bool WindowMaximized { get; set; }

    public bool SortGroupsByName { get; set; } = true;
    public List<string> RecentFiles { get; set; } = [];
    public List<string> FindHistory { get; set; } = [];
    public List<string> ReplaceHistory { get; set; } = [];
    public string FindInFilesDirectory { get; set; } = "";
    public string FindInFilesFilter { get; set; } = "*.*";
    public string FindInFilesExclude { get; set; } = "bin;obj;.git;node_modules;.vs";
}

public sealed class SessionFile
{
    public string Path { get; set; } = "";
    public int CaretOffset { get; set; }
    public double VerticalOffset { get; set; }
    public int? EncodingCodePage { get; set; }
    public bool Pinned { get; set; }
    public string? Syntax { get; set; }
}

public sealed class Session
{
    public List<SessionFile> Files { get; set; } = [];
    public string? ActivePath { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(Session))]
internal partial class SettingsJsonContext : JsonSerializerContext;

public static class SettingsStore
{
    /// <summary>%APPDATA%/Eel-ditor, or EELDITOR_CONFIG_DIR when set (tests, portable use).</summary>
    public static string Directory { get; private set; } =
        Environment.GetEnvironmentVariable("EELDITOR_CONFIG_DIR") is { Length: > 0 } custom
            ? custom
            : DefaultDirectory();

    /// <summary>%APPDATA%/Eel-ditor (~/.config/Eel-ditor).</summary>
    private static string DefaultDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.FolderName);

    /// <summary>Tests: start from an empty settings directory.</summary>
    internal static void UseDirectory(string directory) => Directory = directory;

    private static string SettingsPath => Path.Combine(Directory, "settings.json");
    private static string SessionPath => Path.Combine(Directory, "session.json");

    public static AppSettings Load()
    {
        var settings = Read(SettingsPath, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
        // Cascadia Mono/Code are installed as variable fonts; the editor draws their bold as regular, so Markdown
        // bold would not show. Switch to the default once (the user can pick another font afterwards).
        if (!settings.BoldFontChecked)
        {
            if (settings.FontFamily.TrimStart().StartsWith("Cascadia", StringComparison.OrdinalIgnoreCase))
                settings.FontFamily = AppSettings.DefaultFontFamily;
            settings.BoldFontChecked = true;
        }
        return settings;
    }

    public static void Save(AppSettings settings) => Write(SettingsPath, settings, SettingsJsonContext.Default.AppSettings);

    public static Session LoadSession() => Read(SessionPath, SettingsJsonContext.Default.Session) ?? new Session();

    public static void SaveSession(Session session) => Write(SessionPath, session, SettingsJsonContext.Default.Session);

    private static T? Read<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize(File.ReadAllText(path), info);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Write<T>(string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, info));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings are best effort.
        }
    }
}
