using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WingetGui.Models;

public partial class PackageItem : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _id = string.Empty;

    [ObservableProperty]
    private string _version = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateBadgeText))]
    private string _availableVersion = string.Empty;

    [ObservableProperty]
    private string _source = string.Empty;

    [ObservableProperty]
    private long? _estimatedSizeInKb;

    [ObservableProperty]
    private string _formattedSize = string.Empty;

    [ObservableProperty]
    private string _installDate = string.Empty;

    [ObservableProperty]
    private string _publisher = string.Empty;

    [ObservableProperty]
    private string _installLocation = string.Empty;

    [ObservableProperty]
    private string _architecture = string.Empty;

    [ObservableProperty]
    private string _uninstallString = string.Empty;

    [ObservableProperty]
    private string _iconPath = string.Empty;

    [ObservableProperty]
    private ImageSource? _icon;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateBadgeText))]
    private bool _hasUpdate;

    [ObservableProperty]
    private bool _isInstalled;

    public bool IsWingetSource => !string.IsNullOrEmpty(Source) || (!Id.StartsWith("ARP\\", StringComparison.OrdinalIgnoreCase) && !Id.StartsWith("MSIX\\", StringComparison.OrdinalIgnoreCase));

    public string UpdateBadgeText => HasUpdate ? $"→ {AvailableVersion}" : string.Empty;

    public string ArchitectureBadgeText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Architecture)) return string.Empty;
            if (Name.Contains($"({Architecture})", StringComparison.OrdinalIgnoreCase) ||
                Name.Contains(Architecture, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }
            return $"({Architecture})";
        }
    }
}
