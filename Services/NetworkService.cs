using System.Net;
using System.Net.NetworkInformation;

namespace WingetGui.Services;

/// <summary>
/// Service providing internet connectivity checks and network change monitoring.
/// </summary>
public class NetworkService : IDisposable
{
    private readonly Timer _debounceTimer;
    private bool _isDisposed;
    private bool _lastReportedState = true;

    public event Action<bool>? ConnectivityChanged;

    public NetworkService()
    {
        _debounceTimer = new Timer(OnDebounceTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);
        try
        {
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        }
        catch { }
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        // Debounce by 2 seconds to allow adapter reconnection to stabilize
        _debounceTimer.Change(2000, Timeout.Infinite);
    }

    private async void OnDebounceTimerElapsed(object? state)
    {
        if (_isDisposed) return;
        var connected = await CheckInternetConnectivityAsync();
        if (connected != _lastReportedState)
        {
            _lastReportedState = connected;
            ConnectivityChanged?.Invoke(connected);
        }
    }

    public static async Task<bool> CheckInternetConnectivityAsync(int timeoutMs = 2000)
    {
        try
        {
            if (!NetworkInterface.GetIsNetworkAvailable())
            {
                return false;
            }

            // Quick DNS resolve probe to confirm actual internet route
            using var cts = new CancellationTokenSource(timeoutMs);
            var hostEntry = await Dns.GetHostEntryAsync("www.msftconnecttest.com", cts.Token);
            return hostEntry.AddressList.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        try
        {
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        }
        catch { }
        _debounceTimer.Dispose();
        GC.SuppressFinalize(this);
    }
}
