using System.Windows;

namespace PrinterAgent.Configurator;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        ConfiguratorCulture.UseEnglish();
        base.OnStartup(e);
    }
}
