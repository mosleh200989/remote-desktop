using System.Windows;
using RemoteHost.Config;
using RemoteHost.Views;

namespace RemoteHost;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var config = HostConfig.Load();
        if (config is null || !config.IsRegistered)
        {
            var setup = new SetupWindow();
            var ok = setup.ShowDialog();
            if (ok != true || setup.ResultConfig is null)
            {
                Shutdown();
                return;
            }
            config = setup.ResultConfig;
        }

        var main = new MainWindow(config);
        MainWindow = main;
        main.Show();
    }
}
