using System.Windows;
using WingetGui.Services;

namespace WingetGui;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public static string[] StartupArgs { get; private set; } = Array.Empty<string>();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        StartupArgs = e.Args;

        if (!ElevationService.IsRunningAsAdministrator())
        {
            var argsStr = e.Args.Length > 0 ? string.Join(" ", e.Args) : null;
            if (ElevationService.RestartAsAdministrator(argsStr))
            {
                Shutdown();
                return;
            }

            MessageBox.Show(
                "WinGet GUI requires Administrator privileges to manage system packages.\nThe application will now exit.",
                "WinGet GUI",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            Shutdown();
            return;
        }

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }
}
