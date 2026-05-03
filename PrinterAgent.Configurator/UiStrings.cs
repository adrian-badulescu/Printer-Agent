using System.Globalization;
using System.Resources;

namespace PrinterAgent.Configurator;

public static class UiStrings
{
    private static readonly ResourceManager ResourceManager =
        new("PrinterAgent.Configurator.Resources.Strings", typeof(UiStrings).Assembly);

    public static string Get(string key)
        => ResourceManager.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    public static string Format(string key, params object[] args)
        => string.Format(CultureInfo.CurrentUICulture, Get(key), args);
}

