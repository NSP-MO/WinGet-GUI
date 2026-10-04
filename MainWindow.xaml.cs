using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WingetGui.Models;
using WingetGui.Services;
using WingetGui.ViewModels;

namespace WingetGui;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnMainWindowLoaded;
        KeyDown += OnMainWindowKeyDown;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        if (Resources["PackageContextMenu"] is ContextMenu cm)
        {
            cm.DataContext = ViewModel;
        }

        ViewModel.SelectAllRequested = HandleSelectAll;
        Closed += (s, e) => (DataContext as IDisposable)?.Dispose();
    }

    private async void OnMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();
        AutoFitColumns();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.UpgradesCount) ||
            e.PropertyName == nameof(MainViewModel.InstalledPackages) ||
            (e.PropertyName == nameof(MainViewModel.IsLoading) && !ViewModel.IsLoading))
        {
            Dispatcher.InvokeAsync(AutoFitColumns, System.Windows.Threading.DispatcherPriority.Background);
        }
        else if (e.PropertyName == nameof(MainViewModel.SelectedTabIndex))
        {
            SyncActiveListSelection();
        }
        else if (e.PropertyName == nameof(MainViewModel.DrawerOutput))
        {
            ConsoleScrollViewer?.ScrollToEnd();
        }
    }

    private void SyncActiveListSelection()
    {
        if (ViewModel.SelectedTabIndex == 0 && InstalledListView != null)
        {
            ViewModel.UpdateSelectedItems(InstalledListView.SelectedItems.OfType<PackageItem>().ToList());
        }
        else if (ViewModel.SelectedTabIndex == 1 && UpdatesListView != null)
        {
            ViewModel.UpdateSelectedItems(UpdatesListView.SelectedItems.OfType<PackageItem>().ToList());
        }
        else
        {
            ViewModel.UpdateSelectedItems(new List<PackageItem>());
        }
    }

    private void HandleSelectAll()
    {
        if (ViewModel.SelectedTabIndex == 0 && InstalledListView != null)
        {
            InstalledListView.SelectAll();
            InstalledListView.Focus();
        }
        else if (ViewModel.SelectedTabIndex == 1 && UpdatesListView != null)
        {
            UpdatesListView.SelectAll();
            UpdatesListView.Focus();
        }
    }

    private void AutoFitColumns()
    {
        if (InstalledListView?.View is GridView gv)
        {
            foreach (var col in gv.Columns)
            {
                if (double.IsNaN(col.Width))
                {
                    col.Width = col.ActualWidth;
                    col.Width = double.NaN;
                }
            }
        }
        if (UpdatesListView?.View is GridView ugv)
        {
            foreach (var col in ugv.Columns)
            {
                if (double.IsNaN(col.Width))
                {
                    col.Width = col.ActualWidth;
                    col.Width = double.NaN;
                }
            }
        }
    }

    private void OnMainWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.D1)
            {
                ViewModel.SelectedTabIndex = 0;
                e.Handled = true;
            }
            else if (e.Key == Key.D2)
            {
                ViewModel.SelectedTabIndex = 1;
                e.Handled = true;
            }
            else if (e.Key == Key.D3)
            {
                ViewModel.SelectedTabIndex = 2;
                e.Handled = true;
            }
            else if (e.Key == Key.F)
            {
                if (ViewModel.SelectedTabIndex == 2)
                {
                    OnlineSearchBox.Focus();
                    OnlineSearchBox.SelectAll();
                }
                else
                {
                    QuickSearchBox.Focus();
                    QuickSearchBox.SelectAll();
                }
                e.Handled = true;
            }
            else if (e.Key == Key.E)
            {
                ViewModel.ExportListCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.A)
            {
                if (!QuickSearchBox.IsFocused && !OnlineSearchBox.IsFocused)
                {
                    ViewModel.SelectAllCommand.Execute(null);
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.U)
            {
                if (ViewModel.SelectedItem != null || ViewModel.SelectedItems.Count > 0)
                {
                    ViewModel.UpgradeCommand.Execute(ViewModel.SelectedItem);
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.H || e.Key == Key.P)
            {
                if (ViewModel.SelectedTabIndex == 1 && (ViewModel.SelectedItem != null || ViewModel.SelectedItems.Count > 0))
                {
                    ViewModel.TogglePinCommand.Execute(ViewModel.SelectedItem);
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.O)
            {
                if (ViewModel.SelectedItem != null)
                {
                    ViewModel.OpenLocationCommand.Execute(ViewModel.SelectedItem);
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.C)
            {
                if (ViewModel.SelectedItem != null && !QuickSearchBox.IsFocused)
                {
                    ViewModel.CopyIdCommand.Execute(ViewModel.SelectedItem);
                    e.Handled = true;
                }
            }
        }
        else if (e.Key == Key.F5)
        {
            ViewModel.RefreshCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            if (ViewModel.SelectedItem != null && !QuickSearchBox.IsFocused && !OnlineSearchBox.IsFocused)
            {
                if (Keyboard.Modifiers == ModifierKeys.Shift)
                {
                    ViewModel.ForceRemovalCommand.Execute(ViewModel.SelectedItem);
                }
                else
                {
                    ViewModel.UninstallCommand.Execute(ViewModel.SelectedItem);
                }
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Enter)
        {
            if (ViewModel.SelectedItem != null && !QuickSearchBox.IsFocused && !OnlineSearchBox.IsFocused)
            {
                ViewModel.ShowDetailsCommand.Execute(ViewModel.SelectedItem);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape)
        {
            if (ViewModel.IsDetailsOpen)
            {
                ViewModel.CloseDetailsCommand.Execute(null);
                e.Handled = true;
            }
            else if (ViewModel.IsDrawerOpen && !ViewModel.IsDrawerRunning)
            {
                ViewModel.CloseDrawerCommand.Execute(null);
                e.Handled = true;
            }
        }
    }

    private void OnListViewItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.SelectedItem != null)
        {
            ViewModel.ShowDetailsCommand.Execute(ViewModel.SelectedItem);
        }
    }

    private void OnListViewSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView lv)
        {
            var selected = lv.SelectedItems.OfType<PackageItem>().ToList();
            ViewModel.UpdateSelectedItems(selected);
        }
    }

    private void OnListViewItemPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem item)
        {
            if (!item.IsSelected)
            {
                if (ItemsControl.ItemsControlFromItemContainer(item) is ListView lv)
                {
                    lv.SelectedItems.Clear();
                }
                item.IsSelected = true;
            }
            item.Focus();
        }
    }

    private void OnListViewContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is ListView lv)
        {
            var selected = lv.SelectedItems.OfType<PackageItem>().ToList();
            ViewModel.UpdateSelectedItems(selected);
        }

        if (ViewModel.SelectedItem == null && ViewModel.SelectedItems.Count == 0)
        {
            e.Handled = true;
            return;
        }

        if (Resources["PackageContextMenu"] is ContextMenu cm)
        {
            cm.DataContext = ViewModel;
        }
    }

    private void OnQuickSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ViewModel.SearchText = string.Empty;
            InstalledListView.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && ViewModel.SelectedTabIndex == 2)
        {
            ViewModel.SearchOnlineCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnOnlineSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ViewModel.SearchOnlineCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ViewModel.OnlineSearchText = string.Empty;
            e.Handled = true;
        }
    }

    private void OnMenuExitClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnMenuAboutClick(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            $"WinGet GUI\nVersion 1.0.0\n\nA modern, lightweight desktop package manager interface for Windows Package Manager (winget).\nEngine: Winget {ViewModel.WingetVersion}",
            "About WinGet GUI",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OnViewInstalledClick(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedTabIndex = 0;
    }

    private void OnViewUpdatesClick(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedTabIndex = 1;
    }

    private void OnViewDiscoverClick(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedTabIndex = 2;
        Dispatcher.BeginInvoke(() =>
        {
            OnlineSearchBox.Focus();
            OnlineSearchBox.SelectAll();
        });
    }

    private void OnTabInstalledChecked(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedTabIndex = 0;
    }

    private void OnTabUpdatesChecked(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedTabIndex = 1;
    }

    private void OnTabDiscoverChecked(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedTabIndex = 2;
        Dispatcher.BeginInvoke(() =>
        {
            OnlineSearchBox.Focus();
            OnlineSearchBox.SelectAll();
        });
    }

    private async void OnCopyDetailsClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ViewModel.DetailsContent)) return;

        bool success = ClipboardService.TrySetText(ViewModel.DetailsContent);
        if (success && sender is Button btn)
        {
            var originalContent = btn.Content;
            btn.Content = "Copied!";
            btn.IsEnabled = false;
            await Task.Delay(1200);
            btn.Content = originalContent;
            btn.IsEnabled = true;
        }
    }

    private readonly Dictionary<object, ScrollViewer> _scrollViewerCache = new();

    private void OnListViewPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            if (sender is DependencyObject dep)
            {
                if (!_scrollViewerCache.TryGetValue(sender, out var scrollViewer))
                {
                    scrollViewer = (dep as ScrollViewer) ?? FindVisualChild<ScrollViewer>(dep);
                    if (scrollViewer != null)
                    {
                        _scrollViewerCache[sender] = scrollViewer;
                    }
                }

                if (scrollViewer != null)
                {
                    if (scrollViewer.ScrollableWidth > 0)
                    {
                        double targetOffset = scrollViewer.HorizontalOffset - (e.Delta * 0.5);
                        scrollViewer.ScrollToHorizontalOffset(targetOffset);
                    }
                    e.Handled = true;
                }
            }
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null) return null;
        int childrenCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childrenCount; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
                return typedChild;
            var result = FindVisualChild<T>(child);
            if (result != null)
                return result;
        }
        return null;
    }
}