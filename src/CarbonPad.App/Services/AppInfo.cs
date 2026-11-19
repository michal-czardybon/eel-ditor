namespace CarbonPad.App.Services;

/// <summary>The user-visible name of the application (window titles, dialogs, installer).</summary>
public static class AppInfo
{
    public const string Name = "Carbon Editor";

    /// <summary>Settings folder name (under %APPDATA% / ~/.config).</summary>
    public const string FolderName = "Carbon Editor";

    /// <summary>The settings folder used before the rename; migrated on first start.</summary>
    public const string LegacyFolderName = "CarbonPad";
}
