using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace SinsEditor.App;

/// <summary>
/// Title, logo and copyright line. Puts <c>AppTitle</c>, <c>Logo</c> (an image for the current light/dark theme) and <c>CopyrightText</c>
/// into the application resources, and swaps the logo when Windows switches between light and dark.
/// </summary>
public static class Branding
{
    public const string AppTitle = "Sins of Slop Editor";

    /// <summary>The hyphen in "lt‑design" is non-breaking, so narrow places wrap after the slash instead.</summary>
    public static string CopyrightText => $"© {DateTime.Now.Year} Tobias Liebhart / lt‑design";

    public static void Apply(Application app)
    {
        app.Resources["AppTitle"] = AppTitle;
        app.Resources["CopyrightText"] = CopyrightText;
        UpdateLogo(app);
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
                app.Dispatcher.BeginInvoke(() => UpdateLogo(app));
        };
    }

    /// <summary>Windows apps theme: AppsUseLightTheme = 0 means dark (the app follows it via ThemeMode="System").</summary>
    public static bool IsDarkTheme =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0;

    private static void UpdateLogo(Application app)
    {
        string file = IsDarkTheme ? "Logo-White.png" : "Logo.png";
        var logo = new BitmapImage();
        logo.BeginInit();
        // Resources are addressed by assembly name (SinsOfSlopEditor), so read it instead of hard-coding it.
        string assembly = typeof(Branding).Assembly.GetName().Name!;
        logo.UriSource = new Uri($"pack://application:,,,/{assembly};component/Assets/{file}");
        logo.CacheOption = BitmapCacheOption.OnLoad;
        logo.EndInit();
        logo.Freeze();
        app.Resources["Logo"] = logo;
    }
}
