using Avalonia.Media;

namespace EelDitor.App.Views;

/// <summary>Text trimming used in XAML via x:Static.</summary>
public static class Trimming
{
    /// <summary>Cuts from the start ("…\src\Views"), so the end of a path, its most specific part, stays visible.</summary>
    public static TextTrimming Leading { get; } = new TextLeadingPrefixTrimming("…", 0);
}
