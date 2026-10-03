using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WingetGui.Models;
using WingetGui.Services;

namespace WingetGui.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly WingetService _wingetService;
    private CancellationTokenSource? _operationCts;

    public ObservableCollection<PackageItem> InstalledPackages { get; } = new();
    public ObservableCollection<PackageItem> UpgradePackages { get; } = new();
    public ObservableCollection<PackageItem> SearchResults { get; } = new();

    public ICollectionView FilteredInstalledPackages { get; }
    public ICollectionView FilteredUpgradePackages { get; }

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private int _selectedTabIndex = 0;

    [ObservableProperty]
    private PackageItem? _selectedItem;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _loadingStatus = string.Empty;

    [ObservableProperty]
    private string _statusSummary = string.Empty;

    [ObservableProperty]
    private int _installedCount;

    [ObservableProperty]
    private int _upgradesCount;

    [ObservableProperty]
    private string _installedTabTitle = "Installed";

    [ObservableProperty]
    private string _updatesTabTitle = "Updates";

    [ObservableProperty]
    private string _wingetVersion = string.Empty;

    // Operation drawer / modal properties
    [ObservableProperty]
    private bool _isDrawerOpen;

    [ObservableProperty]
    private string _drawerTitle = string.Empty;

    [ObservableProperty]
    private string _drawerOutput = string.Empty;

    [ObservableProperty]
    private bool _isDrawerRunning;

    // Details flyout properties
    [ObservableProperty]
    private bool _isDetailsOpen;

    [ObservableProperty]
    private string _detailsTitle = string.Empty;

    [ObservableProperty]
    private string _detailsContent = string.Empty;

    public MainViewModel()
    {
        _wingetService = new WingetService();

        FilteredInstalledPackages = CollectionViewSource.GetDefaultView(InstalledPackages);
        FilteredInstalledPackages.Filter = FilterInstalledPredicate;

        FilteredUpgradePackages = CollectionViewSource.GetDefaultView(UpgradePackages);
        FilteredUpgradePackages.Filter = FilterUpgradePredicate;
    }

    partial void OnSearchTextChanged(string value)
    {
        FilteredInstalledPackages.Refresh();
        FilteredUpgradePackages.Refresh();
        UpdateStatusSummary();
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        UpdateStatusSummary();
    }

    private bool FilterInstalledPredicate(object obj)
    {
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        if (obj is not PackageItem item) return false;

        var term = SearchText.Trim();
        return item.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
               item.Id.Contains(term, StringComparison.OrdinalIgnoreCase) ||
               item.Publisher.Contains(term, StringComparison.OrdinalIgnoreCase) ||
               item.Version.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    private bool FilterUpgradePredicate(object obj)
    {
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        if (obj is not PackageItem item) return false;

        var term = SearchText.Trim();
        return item.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
               item.Id.Contains(term, StringComparison.OrdinalIgnoreCase) ||
               item.Publisher.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    public async Task InitializeAsync()
    {
        var ver = await _wingetService.GetWingetVersionAsync();
        WingetVersion = ver ?? "Unknown";
        await RefreshAsync();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsLoading) return;

        IsLoading = true;
        LoadingStatus = "Loading installed packages...";

        try
        {
            var installedTask = _wingetService.GetInstalledPackagesAsync();
            var upgradesTask = _wingetService.GetAvailableUpgradesAsync();

            await Task.WhenAll(installedTask, upgradesTask);

            var installed = await installedTask;
            var upgrades = await upgradesTask;

            // Merge upgrade info into installed packages
            var upgradeMap = upgrades.ToDictionary(u => u.Id, u => u.AvailableVersion, StringComparer.OrdinalIgnoreCase);
            foreach (var item in installed)
            {
                if (upgradeMap.TryGetValue(item.Id, out var newVer))
                {
                    item.HasUpdate = true;
                    item.AvailableVersion = newVer;
                }
            }

            InstalledPackages.Clear();
            foreach (var item in installed)
            {
                InstalledPackages.Add(item);
            }

            UpgradePackages.Clear();
            foreach (var item in upgrades)
            {
                UpgradePackages.Add(item);
            }

            InstalledCount = InstalledPackages.Count;
            UpgradesCount = UpgradePackages.Count;

            InstalledTabTitle = $"Installed ({InstalledCount})";
            UpdatesTabTitle = $"Updates ({UpgradesCount})";

            UpdateStatusSummary();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load packages: {ex.Message}", "WinGet GUI", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsLoading = false;
            LoadingStatus = string.Empty;
        }
    }

    private void UpdateStatusSummary()
    {
        long totalKb = 0;
        int knownSizeCount = 0;

        foreach (var item in InstalledPackages)
        {
            if (item.EstimatedSizeInKb.HasValue && item.EstimatedSizeInKb.Value > 0)
            {
                totalKb += item.EstimatedSizeInKb.Value;
                knownSizeCount++;
            }
        }

        var totalSizeStr = totalKb > 0 ? RegistryService.FormatSize(totalKb) : "0 MB";

        if (SelectedTabIndex == 0)
        {
            var filteredCount = FilteredInstalledPackages.Cast<object>().Count();
            if (string.IsNullOrWhiteSpace(SearchText))
            {
                StatusSummary = $"{InstalledCount} programs of {totalSizeStr} size in total";
            }
            else
            {
                StatusSummary = $"Showing {filteredCount} of {InstalledCount} programs";
            }
        }
        else if (SelectedTabIndex == 1)
        {
            var filteredCount = FilteredUpgradePackages.Cast<object>().Count();
            StatusSummary = string.IsNullOrWhiteSpace(SearchText)
                ? $"{UpgradesCount} updates available"
                : $"Showing {filteredCount} of {UpgradesCount} updates";
        }
        else
        {
            StatusSummary = $"{SearchResults.Count} packages found";
        }
    }

    [RelayCommand]
    public async Task SearchOnlineAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchText)) return;

        IsLoading = true;
        LoadingStatus = $"Searching winget repository for \"{SearchText}\"...";

        try
        {
            var results = await _wingetService.SearchPackagesAsync(SearchText, 40);
            SearchResults.Clear();
            foreach (var res in results)
            {
                SearchResults.Add(res);
            }
            SelectedTabIndex = 2; // Switch to Discover tab
            UpdateStatusSummary();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Search failed: {ex.Message}", "WinGet GUI", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsLoading = false;
            LoadingStatus = string.Empty;
        }
    }

    [RelayCommand]
    public async Task UninstallAsync(object? param)
    {
        var item = param as PackageItem ?? SelectedItem;
        if (item == null) return;

        var confirm = MessageBox.Show(
            $"Are you sure you want to uninstall \"{item.Name}\" ({item.Version})?",
            "Confirm Uninstall",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        var arg = item.IsWingetSource
            ? $"uninstall --id \"{item.Id}\" --accept-source-agreements"
            : $"uninstall --name \"{item.Name}\"";

        await RunWingetOperationAsync($"Uninstalling {item.Name}...", arg, true);
    }

    [RelayCommand]
    public async Task UpgradeAsync(object? param)
    {
        var item = param as PackageItem ?? SelectedItem;
        if (item == null) return;

        var arg = $"upgrade --id \"{item.Id}\" --accept-source-agreements --accept-package-agreements";
        await RunWingetOperationAsync($"Upgrading {item.Name} to {item.AvailableVersion}...", arg, true);
    }

    [RelayCommand]
    public async Task UpgradeAllAsync()
    {
        if (UpgradesCount == 0)
        {
            MessageBox.Show("No package updates available.", "WinGet GUI", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"Upgrade all {UpgradesCount} packages to their latest versions?",
            "Confirm Upgrade All",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        var arg = "upgrade --all --accept-source-agreements --accept-package-agreements";
        await RunWingetOperationAsync("Upgrading All Packages...", arg, true);
    }

    [RelayCommand]
    public async Task InstallAsync(object? param)
    {
        var item = param as PackageItem ?? SelectedItem;
        if (item == null) return;

        var arg = $"install --id \"{item.Id}\" --accept-source-agreements --accept-package-agreements";
        await RunWingetOperationAsync($"Installing {item.Name}...", arg, true);
    }

    [RelayCommand]
    public async Task ShowDetailsAsync(object? param)
    {
        var item = param as PackageItem ?? SelectedItem;
        if (item == null) return;

        IsLoading = true;
        LoadingStatus = $"Fetching package details for {item.Name}...";

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Package Name:  {item.Name}");
            sb.AppendLine($"Identifier:    {item.Id}");
            sb.AppendLine($"Version:       {item.Version}");
            if (item.HasUpdate)
            {
                sb.AppendLine($"Available:     {item.AvailableVersion}");
            }
            if (!string.IsNullOrWhiteSpace(item.Publisher))
            {
                sb.AppendLine($"Publisher:     {item.Publisher}");
            }
            if (!string.IsNullOrWhiteSpace(item.FormattedSize))
            {
                sb.AppendLine($"Estimated Size:{item.FormattedSize}");
            }
            if (!string.IsNullOrWhiteSpace(item.InstallDate))
            {
                sb.AppendLine($"Installed On:  {item.InstallDate}");
            }
            if (!string.IsNullOrWhiteSpace(item.Architecture))
            {
                sb.AppendLine($"Architecture:  {item.Architecture}");
            }
            if (!string.IsNullOrWhiteSpace(item.InstallLocation))
            {
                sb.AppendLine($"Location:      {item.InstallLocation}");
            }
            if (!string.IsNullOrWhiteSpace(item.UninstallString))
            {
                sb.AppendLine($"Uninstall Cmd: {item.UninstallString}");
            }

            if (item.IsWingetSource && !item.Id.StartsWith("ARP\\", StringComparison.OrdinalIgnoreCase))
            {
                var raw = await _wingetService.GetPackageDetailsAsync(item.Id);
                if (!string.IsNullOrWhiteSpace(raw) && !raw.Contains("No package found matching", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine();
                    sb.AppendLine(new string('-', 50));
                    sb.AppendLine("Windows Package Manager Metadata:");
                    sb.AppendLine(new string('-', 50));
                    sb.AppendLine(raw.Trim());
                }
            }

            DetailsTitle = $"{item.Name} Details";
            DetailsContent = sb.ToString();
            IsDetailsOpen = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to fetch details: {ex.Message}", "WinGet GUI", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsLoading = false;
            LoadingStatus = string.Empty;
        }
    }

    [RelayCommand]
    public void OpenLocation(object? param)
    {
        var item = param as PackageItem ?? SelectedItem;
        if (item == null) return;

        if (!string.IsNullOrWhiteSpace(item.InstallLocation) && Directory.Exists(item.InstallLocation))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = item.InstallLocation,
                UseShellExecute = true
            });
            return;
        }

        if (!string.IsNullOrWhiteSpace(item.IconPath))
        {
            var clean = item.IconPath.Trim('\"');
            var comma = clean.LastIndexOf(',');
            if (comma > 0) clean = clean[..comma];
            clean = Environment.ExpandEnvironmentVariables(clean);

            if (File.Exists(clean))
            {
                Process.Start("explorer.exe", $"/select,\"{clean}\"");
                return;
            }
        }

        MessageBox.Show("Installation directory not found in registry.", "WinGet GUI", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    [RelayCommand]
    public void CopyId(object? param)
    {
        var item = param as PackageItem ?? SelectedItem;
        if (item == null) return;

        Clipboard.SetText(item.Id);
    }

    [RelayCommand]
    public void SearchWeb(object? param)
    {
        var item = param as PackageItem ?? SelectedItem;
        if (item == null) return;

        var url = $"https://www.google.com/search?q={Uri.EscapeDataString(item.Name)}";
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    [RelayCommand]
    public void ExportList()
    {
        try
        {
            var sfd = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Text File (*.txt)|*.txt|CSV File (*.csv)|*.csv",
                FileName = $"InstalledPackages_{DateTime.Now:yyyyMMdd}.txt"
            };

            if (sfd.ShowDialog() == true)
            {
                var sb = new StringBuilder();
                if (sfd.FilterIndex == 2)
                {
                    // CSV
                    sb.AppendLine("Name,Id,Version,Available,Size,InstalledOn,Publisher");
                    foreach (var p in InstalledPackages)
                    {
                        sb.AppendLine($"\"{p.Name}\",\"{p.Id}\",\"{p.Version}\",\"{p.AvailableVersion}\",\"{p.FormattedSize}\",\"{p.InstallDate}\",\"{p.Publisher}\"");
                    }
                }
                else
                {
                    // Text
                    sb.AppendLine($"Installed Packages ({InstalledPackages.Count}) - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    sb.AppendLine(new string('-', 100));
                    sb.AppendLine(string.Format("{0,-40} {1,-35} {2,-15} {3,-10}", "Name", "Id", "Version", "Size"));
                    sb.AppendLine(new string('-', 100));
                    foreach (var p in InstalledPackages)
                    {
                        sb.AppendLine(string.Format("{0,-40} {1,-35} {2,-15} {3,-10}",
                            p.Name.Length > 38 ? p.Name[..38] : p.Name,
                            p.Id.Length > 33 ? p.Id[..33] : p.Id,
                            p.Version,
                            p.FormattedSize));
                    }
                }

                File.WriteAllText(sfd.FileName, sb.ToString(), Encoding.UTF8);
                MessageBox.Show($"Exported successfully to {sfd.FileName}", "WinGet GUI", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Export failed: {ex.Message}", "WinGet GUI", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    public void ClearSearch()
    {
        SearchText = string.Empty;
    }

    [RelayCommand]
    public void CloseDrawer()
    {
        if (IsDrawerRunning) return;
        IsDrawerOpen = false;
    }

    [RelayCommand]
    public void CloseDetails()
    {
        IsDetailsOpen = false;
    }

    private async Task RunWingetOperationAsync(string title, string args, bool refreshOnSuccess)
    {
        DrawerTitle = title;
        DrawerOutput = $"Starting operation: winget {args}\n\n";
        IsDrawerOpen = true;
        IsDrawerRunning = true;

        _operationCts = new CancellationTokenSource();

        try
        {
            var exitCode = await _wingetService.StreamCommandAsync(args, line =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    DrawerOutput += line + "\n";
                });
            }, _operationCts.Token);

            DrawerOutput += $"\nProcess completed with exit code: {exitCode}\n";

            if (exitCode == 0 && refreshOnSuccess)
            {
                await RefreshAsync();
            }
        }
        catch (OperationCanceledException)
        {
            DrawerOutput += "\nOperation was cancelled by user.\n";
        }
        catch (Exception ex)
        {
            DrawerOutput += $"\nError executing command: {ex.Message}\n";
        }
        finally
        {
            IsDrawerRunning = false;
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }
}
