// UI language: follows the system by default (Russian system -> Russian UI,
// anything else -> English) and can be forced from the tray menu.

using System.Globalization;

namespace GigaPisar.App;

public enum UiLanguage { Auto, Russian, English }

public static class L
{
    public static bool Russian { get; private set; } = true;

    public static void Apply(UiLanguage choice)
    {
        Russian = choice switch
        {
            UiLanguage.Russian => true,
            UiLanguage.English => false,
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>Picks the Russian or English text for the current UI language.</summary>
    public static string T(string ru, string en) => Russian ? ru : en;
}
