using System.Globalization;

namespace PrinterAgent.Configurator;

/// <summary>Installer wizard uses English regardless of Windows display language.</summary>
internal static class ConfiguratorCulture
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    public static void UseEnglish()
    {
        CultureInfo.CurrentUICulture = English;
        CultureInfo.DefaultThreadCurrentUICulture = English;
        CultureInfo.CurrentCulture = English;
        CultureInfo.DefaultThreadCurrentCulture = English;
    }
}
