using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;

namespace WingetGui.Services;

/// <summary>
/// Service for Windows Administrator elevation management and on-demand restart.
/// </summary>
public static class ElevationService
{
    public static bool IsRunningAsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static bool EnsureElevated(string? resumeArgs = null)
    {
        if (IsRunningAsAdministrator())
        {
            return true;
        }

        var result = MessageBox.Show(
            "Administrator privileges are required to perform package management operations.\n\nRestart WinGet GUI as Administrator to continue?",
            "Administrator Privileges Required",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            if (RestartAsAdministrator(resumeArgs))
            {
                return false;
            }
            else
            {
                MessageBox.Show(
                    "Administrator privileges were not granted. The operation has been cancelled.",
                    "WinGet GUI",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        return false;
    }

    public static bool RestartAsAdministrator(string? additionalArgs = null)
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(processPath))
            {
                processPath = Process.GetCurrentProcess().MainModule?.FileName;
            }

            if (string.IsNullOrEmpty(processPath)) return false;

            var isDotNetHost = Path.GetFileName(processPath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase);
            var entryAssemblyLocation = System.Reflection.Assembly.GetEntryAssembly()?.Location;

            var psi = new ProcessStartInfo
            {
                UseShellExecute = true,
                Verb = "runas"
            };

            if (isDotNetHost && !string.IsNullOrEmpty(entryAssemblyLocation))
            {
                psi.FileName = processPath;
                psi.Arguments = $"\"{entryAssemblyLocation}\" {additionalArgs ?? string.Empty}".Trim();
            }
            else
            {
                psi.FileName = processPath;
                psi.Arguments = additionalArgs ?? string.Empty;
            }

            Process.Start(psi);
            Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // UAC prompt cancelled by user
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
