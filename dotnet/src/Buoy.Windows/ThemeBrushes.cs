using Microsoft.UI.Xaml;

namespace Buoy.Windows;

/// <summary>
/// Theme resources for UI built in code. <c>Application.Current.Resources[key]</c>
/// answers for the system theme, but Buoy's own Light/Dark setting themes the
/// panel separately, so a code-built row would come out in the wrong theme.
/// This looks the key up in the theme dictionary for the element's actual theme.
/// </summary>
internal static class ThemeBrushes
{
    public static T Get<T>(FrameworkElement element, string key)
    {
        string[] themeKeys = element.ActualTheme == ElementTheme.Dark ? ["Dark", "Default"] : ["Light"];
        return Find(Application.Current.Resources, themeKeys, key, depth: 0) is T found
            ? found
            : (T)Application.Current.Resources[key];
    }

    private static object? Find(ResourceDictionary dictionary, string[] themeKeys, string key, int depth)
    {
        foreach (var themeKey in themeKeys)
        {
            if (dictionary.ThemeDictionaries.TryGetValue(themeKey, out var theme)
                && theme is ResourceDictionary themed
                && themed.TryGetValue(key, out var value))
                return value;
        }
        if (depth > 3) return null;
        foreach (var merged in dictionary.MergedDictionaries)
        {
            if (Find(merged, themeKeys, key, depth + 1) is { } value) return value;
        }
        return null;
    }
}
