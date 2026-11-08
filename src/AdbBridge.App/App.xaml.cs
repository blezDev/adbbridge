using System.Windows;
using Application = System.Windows.Application;

namespace AdbBridge.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length == 2 && e.Args[0] == "--apply-update")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await SelfUpdate.ApplyAsync(e.Args[1]); }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "AdbBridge update failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            Shutdown();
            return;
        }

        if (!SingleInstanceGuard.TryAcquire())
        {
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SingleInstanceGuard.Release();
        base.OnExit(e);
    }
}
