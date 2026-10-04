using System.Collections.ObjectModel;
using System.Windows;
using WingetGui.Models;
using WingetGui.Services;

namespace WingetGui;

public partial class LeftoverReviewWindow : Window
{
    public ObservableCollection<LeftoverItem> Items { get; } = new();

    public LeftoverReviewWindow(string appName, string appVersion, List<LeftoverItem> items)
    {
        InitializeComponent();

        var versionDisplay = string.IsNullOrWhiteSpace(appVersion) ? string.Empty : $" ({appVersion})";
        TxtHeaderTitle.Text = $"Residual Traces for {appName}{versionDisplay}";

        foreach (var item in items)
        {
            Items.Add(item);
        }

        LeftoversListView.ItemsSource = Items;

        int foldersCount = items.Count(i => i.Kind == LeftoverKind.Folder);
        int filesCount = items.Count(i => i.Kind == LeftoverKind.File);
        int regCount = items.Count(i => i.Kind == LeftoverKind.RegistryKey || i.Kind == LeftoverKind.RegistryValue);

        var detailsList = new List<string>();
        if (foldersCount > 0) detailsList.Add($"{foldersCount} folder{(foldersCount > 1 ? "s" : "")}");
        if (filesCount > 0) detailsList.Add($"{filesCount} file{(filesCount > 1 ? "s" : "")}");
        if (regCount > 0) detailsList.Add($"{regCount} registry key{(regCount > 1 ? "s" : "")}");

        var detailsStr = detailsList.Count > 0 ? $" ({string.Join(", ", detailsList)})" : string.Empty;
        TxtItemsCountSummary.Text = $"Found {items.Count} leftover item{(items.Count > 1 ? "s" : "")}{detailsStr}";

        UpdateSelectionSummary();
    }

    private void UpdateSelectionSummary()
    {
        var selected = Items.Where(i => i.IsSelected).ToList();
        long totalBytes = selected.Sum(i => i.SizeInBytes);

        var formattedSize = totalBytes > 0 ? LeftoverCleanerService.FormatBytes(totalBytes) : "0 B";
        TxtSelectedSummary.Text = $"Selected: {selected.Count} of {Items.Count} item(s) ({formattedSize} to be deleted)";

        BtnDelete.IsEnabled = selected.Count > 0;

        if (selected.Count == Items.Count)
        {
            ChkHeaderAll.IsChecked = true;
        }
        else if (selected.Count == 0)
        {
            ChkHeaderAll.IsChecked = false;
        }
        else
        {
            ChkHeaderAll.IsChecked = null;
        }
    }

    private void OnHeaderCheckToggled(object sender, RoutedEventArgs e)
    {
        bool isChecked = ChkHeaderAll.IsChecked == true;
        foreach (var item in Items)
        {
            item.IsSelected = isChecked;
        }
        UpdateSelectionSummary();
    }

    private void OnItemCheckChanged(object sender, RoutedEventArgs e)
    {
        UpdateSelectionSummary();
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var item in Items)
        {
            item.IsSelected = true;
        }
        UpdateSelectionSummary();
    }

    private void OnDeselectAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var item in Items)
        {
            item.IsSelected = false;
        }
        UpdateSelectionSummary();
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnSkipClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
