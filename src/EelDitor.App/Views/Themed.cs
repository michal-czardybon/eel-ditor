using Avalonia;
using Avalonia.Controls;

namespace EelDitor.App.Views;

/// <summary>
/// Binds properties of controls built in code to theme resources, so they follow light/dark switches.
/// (Application.FindResource cannot see theme dictionaries without a theme variant.)
/// </summary>
public static class Themed
{
    public static T Res<T>(this T element, AvaloniaProperty property, string key) where T : StyledElement
    {
        element.Bind(property, element.GetResourceObservable(key));
        return element;
    }
}
