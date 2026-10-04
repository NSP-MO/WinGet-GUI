using System.Windows;
using WingetGui.Services;

namespace WingetGui;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length > 0 && e.Args[0] == "--elevated-worker")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            await ElevatedWorkerService.RunWorkerAsync(e.Args);
            Shutdown();
            return;
        }

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }
}
