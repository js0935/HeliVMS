using System.Windows;
using System.Windows.Interop;

namespace LicenseKeyGen;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var login = new LoginWindow();
        if (login.ShowDialog() == true)
        {
            var main = new MainWindow();
            main.Closed += (_, _) => Shutdown();
            main.Show();
        }
        else
        {
            Shutdown();
        }
    }
}
