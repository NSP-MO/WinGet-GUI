using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using WingetGui.Services;

namespace WingetGui;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Local\WingetGui_SingleInstance_Mutex_5D9C2E7A-28B9-4C4F-94B6-6B2FE3E05D51";
    private static Mutex? _mutex;

    public static string[] StartupArgs { get; private set; } = Array.Empty<string>();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        StartupArgs = e.Args;

        // Single instance check: if another instance is already running, activate it and exit immediately.
        if (!AcquireSingleInstanceMutex())
        {
            BringExistingWindowToFront();
            Shutdown();
            return;
        }

        if (!ElevationService.IsRunningAsAdministrator())
        {
            // Release the mutex before spawning the elevated process so the child won't collide with this parent process
            ReleaseSingleInstanceMutex();

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

    protected override void OnExit(ExitEventArgs e)
    {
        ReleaseSingleInstanceMutex();
        base.OnExit(e);
    }

    private static bool AcquireSingleInstanceMutex()
    {
        try
        {
            _mutex = new Mutex(true, MutexName, out bool createdNew);
            if (!createdNew)
            {
                _mutex.Dispose();
                _mutex = null;
                return false;
            }
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Mutex exists and is held by another process with higher integrity (e.g., Administrator)
            _mutex = null;
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static void ReleaseSingleInstanceMutex()
    {
        if (_mutex != null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch { }
            _mutex.Dispose();
            _mutex = null;
        }
    }

    #region Win32 Window Activation

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    private static void BringExistingWindowToFront()
    {
        try
        {
            IntPtr hWnd = FindExistingWindowHandle();
            if (hWnd != IntPtr.Zero)
            {
                ForceForegroundWindow(hWnd);
            }
        }
        catch
        {
            // Suppress activation failures
        }
    }

    private static IntPtr FindExistingWindowHandle()
    {
        // 1. Direct search by main window title
        IntPtr hWnd = FindWindow(null, "WinGet GUI");
        if (hWnd != IntPtr.Zero) return hWnd;

        // 2. Search processes for top-level window
        try
        {
            var current = Process.GetCurrentProcess();
            var otherPids = Process.GetProcessesByName(current.ProcessName)
                .Where(p => p.Id != current.Id)
                .Select(p => (uint)p.Id)
                .ToHashSet();

            if (otherPids.Count > 0)
            {
                IntPtr foundHwnd = IntPtr.Zero;
                EnumWindows((h, lParam) =>
                {
                    GetWindowThreadProcessId(h, out uint pid);
                    if (otherPids.Contains(pid) && IsWindowVisible(h))
                    {
                        foundHwnd = h;
                        return false; // Stop enumeration
                    }
                    return true;
                }, IntPtr.Zero);

                return foundHwnd;
            }
        }
        catch { }

        return IntPtr.Zero;
    }

    private static void ForceForegroundWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;

        if (IsIconic(hWnd))
        {
            ShowWindow(hWnd, SW_RESTORE);
        }
        else
        {
            ShowWindow(hWnd, SW_SHOW);
        }

        IntPtr fgWnd = GetForegroundWindow();
        uint fgThread = fgWnd != IntPtr.Zero ? GetWindowThreadProcessId(fgWnd, out _) : 0;
        uint appThread = GetCurrentThreadId();

        if (fgThread != 0 && fgThread != appThread)
        {
            AttachThreadInput(appThread, fgThread, true);
            SetForegroundWindow(hWnd);
            AttachThreadInput(appThread, fgThread, false);
        }
        else
        {
            SetForegroundWindow(hWnd);
        }
    }

    #endregion
}
