using CommunityToolkit.Mvvm.ComponentModel;

namespace WingetGui.Models;

public enum LeftoverKind
{
    RegistryKey,
    RegistryValue,
    Folder,
    File
}

public partial class LeftoverItem : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected = true;

    public LeftoverKind Kind { get; init; }
    public string Path { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public long SizeInBytes { get; init; }
    public string FormattedSize { get; init; } = string.Empty;
    public string Details { get; init; } = string.Empty;

    public string KindBadge => Kind switch
    {
        LeftoverKind.RegistryKey => "Registry",
        LeftoverKind.RegistryValue => "Value",
        LeftoverKind.Folder => "Folder",
        LeftoverKind.File => "File",
        _ => "Item"
    };

    public string KindBadgeColor => Kind switch
    {
        LeftoverKind.RegistryKey or LeftoverKind.RegistryValue => "#0E639C", // Technical Blue
        LeftoverKind.Folder => "#B58900", // Folder Amber
        LeftoverKind.File => "#6E6E73",   // Neutral Gray
        _ => "#333333"
    };
}
