using System.Runtime.InteropServices;
using System.Windows;

namespace WingetGui.Services;

public static class ClipboardService
{
    private const int MaxRetries = 10;
    private const int RetryDelayMs = 50;

    /// <summary>
    /// Safely sets text to the Windows clipboard with exponential-friendly retries and
    /// COMException handling to prevent process termination from CLIPBRD_E_CANT_OPEN (0x800401D0).
    /// </summary>
    public static bool TrySetText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        for (int i = 0; i < MaxRetries; i++)
        {
            try
            {
                // copy: false avoids immediate OleFlushClipboard() lock contention
                Clipboard.SetDataObject(text, false);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(RetryDelayMs);
            }
            catch (Exception)
            {
                try
                {
                    Clipboard.SetText(text);
                    return true;
                }
                catch
                {
                    Thread.Sleep(RetryDelayMs);
                }
            }
        }

        return false;
    }
}
