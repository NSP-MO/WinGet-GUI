using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace WingetGui.Services;

public record RegistryAppInfo(
    string DisplayName,
    string? DisplayVersion,
    string? Publisher,
    string? FormattedInstallDate,
    long? EstimatedSizeKb,
    string? FormattedSize,
    string? DisplayIcon,
    string? InstallLocation,
    string? UninstallString,
    string Architecture
);

public static class RegistryService
{
    public static Dictionary<string, RegistryAppInfo> GetInstalledRegistryApps()
    {
        var result = new Dictionary<string, RegistryAppInfo>(StringComparer.OrdinalIgnoreCase);

        var keys = new[]
        {
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", RegistryView.Registry64, "64-bit"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", RegistryView.Registry32, "32-bit"),
            (RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", RegistryView.Default, "User")
        };

        foreach (var (hive, subKeyPath, view, arch) in keys)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(subKeyPath);
                if (key == null) continue;

                foreach (var subKeyName in key.GetSubKeyNames())
                {
                    try
                    {
                        using var appKey = key.OpenSubKey(subKeyName);
                        if (appKey == null) continue;

                        var displayName = appKey.GetValue("DisplayName")?.ToString()?.Trim();
                        if (string.IsNullOrWhiteSpace(displayName)) continue;

                        // Skip system updates or internal components if SystemComponent == 1
                        var sysComp = appKey.GetValue("SystemComponent");
                        if (sysComp is int sysInt && sysInt == 1) continue;

                        var displayVersion = appKey.GetValue("DisplayVersion")?.ToString()?.Trim();
                        var publisher = appKey.GetValue("Publisher")?.ToString()?.Trim();
                        var installDateRaw = appKey.GetValue("InstallDate")?.ToString()?.Trim();
                        var displayIcon = appKey.GetValue("DisplayIcon")?.ToString()?.Trim();
                        var installLocation = appKey.GetValue("InstallLocation")?.ToString()?.Trim();
                        var uninstallString = appKey.GetValue("UninstallString")?.ToString()?.Trim();

                        long? sizeKb = null;
                        var estimatedSize = appKey.GetValue("EstimatedSize");
                        if (estimatedSize is int sizeInt && sizeInt > 0)
                        {
                            sizeKb = sizeInt;
                        }
                        else if (estimatedSize is long sizeLong && sizeLong > 0)
                        {
                            sizeKb = sizeLong;
                        }

                        // Determine architecture from name or registry
                        var actualArch = arch;
                        if (displayName.Contains("(32-bit)", StringComparison.OrdinalIgnoreCase) ||
                            displayName.Contains("x86", StringComparison.OrdinalIgnoreCase))
                        {
                            actualArch = "32-bit";
                        }
                        else if (displayName.Contains("(x64)", StringComparison.OrdinalIgnoreCase) ||
                                 displayName.Contains("(64-bit)", StringComparison.OrdinalIgnoreCase))
                        {
                            actualArch = "64-bit";
                        }

                        var formattedDate = FormatDate(installDateRaw);
                        var formattedSize = sizeKb.HasValue ? FormatSize(sizeKb.Value) : string.Empty;

                        var info = new RegistryAppInfo(
                            displayName,
                            displayVersion,
                            publisher,
                            formattedDate,
                            sizeKb,
                            formattedSize,
                            displayIcon,
                            installLocation,
                            uninstallString,
                            actualArch
                        );

                        // Store under trimmed display name
                        result[displayName] = info;

                        // Also store under cleaned name without architecture tag for easier lookup
                        var cleanName = CleanAppName(displayName);
                        if (!result.ContainsKey(cleanName))
                        {
                            result[cleanName] = info;
                        }
                    }
                    catch
                    {
                        // Skip problematic registry keys
                    }
                }
            }
            catch
            {
                // Skip inaccessible hives
            }
        }

        return result;
    }

    public static RegistryAppInfo? FindApp(Dictionary<string, RegistryAppInfo> registryMap, string appName, string? packageId)
    {
        if (string.IsNullOrWhiteSpace(appName)) return null;

        var trimmed = appName.Trim();
        if (registryMap.TryGetValue(trimmed, out var exact))
        {
            return exact;
        }

        var clean = CleanAppName(trimmed);
        if (registryMap.TryGetValue(clean, out var cleanMatch))
        {
            return cleanMatch;
        }

        // Try prefix match or contains
        foreach (var (key, value) in registryMap)
        {
            if (key.StartsWith(clean, StringComparison.OrdinalIgnoreCase) ||
                clean.StartsWith(key, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        // Try searching by package ID segment if ID is not an ARP string
        if (!string.IsNullOrWhiteSpace(packageId) && !packageId.StartsWith("ARP\\", StringComparison.OrdinalIgnoreCase))
        {
            var segments = packageId.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > 0)
            {
                var lastSegment = segments[^1];
                if (registryMap.TryGetValue(lastSegment, out var idMatch))
                {
                    return idMatch;
                }
            }
        }

        return null;
    }

    public static string FormatSize(long kb)
    {
        if (kb <= 0) return string.Empty;
        if (kb < 1024)
        {
            return $"{kb} KB";
        }

        var mb = (double)kb / 1024.0;
        if (mb < 1024.0)
        {
            return $"{mb:0.##} MB";
        }

        var gb = mb / 1024.0;
        return $"{gb:0.##} GB";
    }

    private static string FormatDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        raw = raw.Trim();

        if (raw.Length == 8 && DateTime.TryParseExact(raw, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            return dt.ToString("MMM dd, yyyy", CultureInfo.InvariantCulture);
        }

        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed.ToString("MMM dd, yyyy", CultureInfo.InvariantCulture);
        }

        return raw;
    }

    private static string CleanAppName(string name)
    {
        // Strip common architecture tags for lookup
        var clean = name
            .Replace("(32-bit)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("(64-bit)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("(x64)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("(x86)", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
        return clean;
    }
}
