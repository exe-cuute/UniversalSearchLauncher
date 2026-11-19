using System.Windows;
using Microsoft.Win32;

namespace UniversalSearchLauncher.Services;

public sealed class ThemeService
{
    private const string DarkSource = "Themes/Dark.xaml";
    private const string LightSource = "Themes/Light.xaml";

    public string AppliedTheme { get; private set; } = "Light";

    public void Apply(string requestedTheme)
    {
        var resolved = Resolve(requestedTheme);
        AppliedTheme = resolved;
        var application = Application.Current;
        if (application is null)
        {
            return;
        }

        var dictionaries = application.Resources.MergedDictionaries;
        for (var i = dictionaries.Count - 1; i >= 0; i--)
        {
            var source = dictionaries[i].Source?.OriginalString;
            if (source is not null &&
                (source.EndsWith(DarkSource, StringComparison.OrdinalIgnoreCase) ||
                 source.EndsWith(LightSource, StringComparison.OrdinalIgnoreCase)))
            {
                dictionaries.RemoveAt(i);
            }
        }

        dictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(resolved == "Dark" ? DarkSource : LightSource, UriKind.Relative)
        });
    }

    public static string Resolve(string requestedTheme)
    {
        if (requestedTheme is "Dark" or "Light")
        {
            return requestedTheme;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                writable: false);
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int intValue && intValue == 0 ? "Dark" : "Light";
        }
        catch
        {
            return "Light";
        }
    }

    public static bool IsDark(string requestedTheme)
    {
        return Resolve(requestedTheme) == "Dark";
    }
}
