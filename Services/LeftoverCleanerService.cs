using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using WingetGui.Models;

namespace WingetGui.Services;

public record CleanResult(
    int TotalProcessed,
    int DeletedCount,
    int FailedCount,
    long TotalBytesFreed,
    List<string> LogEntries,
    List<string> Errors
);

public static class LeftoverCleanerService
{
    private static readonly HashSet<string> ProtectedBasePaths = new(StringComparer.OrdinalIgnoreCase);

    static LeftoverCleanerService()
    {
        InitializeProtectedPaths();
    }

    private static void InitializeProtectedPaths()
    {
        void AddSafe(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                ProtectedBasePaths.Add(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
        }

        AddSafe(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        AddSafe(Environment.GetFolderPath(Environment.SpecialFolder.System));
        AddSafe(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86));
        AddSafe(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        AddSafe(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        AddSafe(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles));
        AddSafe(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86));
        AddSafe(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        AddSafe(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        AddSafe(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        AddSafe(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        AddSafe(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        AddSafe(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
        AddSafe(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu));
        AddSafe(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu));
        AddSafe(Environment.GetFolderPath(Environment.SpecialFolder.Programs));
        AddSafe(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms));
        AddSafe(Path.GetTempPath());
    }

    public static async Task<List<LeftoverItem>> ScanLeftoversAsync(PackageItem item, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var results = new List<LeftoverItem>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var (fullVariants, prodSegments, pubVariants, rawId) = ExtractSearchTerms(item);

            // 1. Check item.InstallLocation if specified and exists on disk
            if (!string.IsNullOrWhiteSpace(item.InstallLocation) && Directory.Exists(item.InstallLocation))
            {
                var fullLoc = Path.GetFullPath(item.InstallLocation);
                if (!IsProtectedDirectory(fullLoc) && seenPaths.Add(fullLoc))
                {
                    var (bytes, count) = CalculateDirectoryMetrics(fullLoc, ct);
                    results.Add(new LeftoverItem
                    {
                        Kind = LeftoverKind.Folder,
                        Path = fullLoc,
                        DisplayName = Path.GetFileName(fullLoc),
                        SizeInBytes = bytes,
                        FormattedSize = FormatBytes(bytes),
                        Details = $"{count} file(s) in install location"
                    });
                }
            }

            // 2. Scan Standard Application Directories
            var appRoots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };

            foreach (var root in appRoots)
            {
                if (ct.IsCancellationRequested) break;
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;

                try
                {
                    var directories = Directory.GetDirectories(root);
                    foreach (var dir in directories)
                    {
                        if (ct.IsCancellationRequested) break;
                        var dirName = Path.GetFileName(dir);
                        if (string.IsNullOrWhiteSpace(dirName)) continue;

                        // Direct application match in root directory
                        if (IsDirectAppMatch(dirName, fullVariants))
                        {
                            var fullPath = Path.GetFullPath(dir);
                            if (!IsProtectedDirectory(fullPath) && seenPaths.Add(fullPath))
                            {
                                var (bytes, count) = CalculateDirectoryMetrics(fullPath, ct);
                                results.Add(new LeftoverItem
                                {
                                    Kind = LeftoverKind.Folder,
                                    Path = fullPath,
                                    DisplayName = dirName,
                                    SizeInBytes = bytes,
                                    FormattedSize = FormatBytes(bytes),
                                    Details = $"{count} file(s) in application folder"
                                });
                            }
                            continue;
                        }

                        // Publisher directory match (e.g. Google, Mozilla, Microsoft)
                        if (IsPublisherMatch(dirName, pubVariants))
                        {
                            try
                            {
                                var subDirs = Directory.GetDirectories(dir);
                                foreach (var sub in subDirs)
                                {
                                    if (ct.IsCancellationRequested) break;
                                    var subName = Path.GetFileName(sub);
                                    if (string.IsNullOrWhiteSpace(subName)) continue;

                                    if (IsDirectAppMatch(subName, fullVariants) || IsProductSegmentMatch(subName, prodSegments))
                                    {
                                        var fullPath = Path.GetFullPath(sub);
                                        if (!IsProtectedDirectory(fullPath) && seenPaths.Add(fullPath))
                                        {
                                            var (bytes, count) = CalculateDirectoryMetrics(fullPath, ct);
                                            results.Add(new LeftoverItem
                                            {
                                                Kind = LeftoverKind.Folder,
                                                Path = fullPath,
                                                DisplayName = $"{dirName}\\{subName}",
                                                SizeInBytes = bytes,
                                                FormattedSize = FormatBytes(bytes),
                                                Details = $"{count} file(s) in publisher folder"
                                            });
                                        }
                                    }
                                }
                            }
                            catch
                            {
                                // Skip inaccessible publisher subdirectories
                            }
                        }
                    }
                }
                catch
                {
                    // Skip inaccessible root directories
                }
            }

            // 3. Scan Shortcuts (Start Menu & Desktop)
            ScanShortcuts(fullVariants, prodSegments, pubVariants, results, seenPaths, ct);

            // 4. Scan Registry Software Keys
            ScanSoftwareRegistry(fullVariants, prodSegments, pubVariants, results, seenPaths, ct);

            // 5. Scan ARP (Add/Remove Programs) Uninstall Keys
            ScanArpRegistry(item.Name, rawId, fullVariants, results, seenPaths, ct);

            // Sort results: Folders first by size descending, then files, then registry
            return results
                .OrderBy(r => r.Kind switch
                {
                    LeftoverKind.Folder => 0,
                    LeftoverKind.File => 1,
                    LeftoverKind.RegistryKey => 2,
                    _ => 3
                })
                .ThenByDescending(r => r.SizeInBytes)
                .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, ct);
    }

    private static void ScanShortcuts(
        HashSet<string> fullVariants,
        HashSet<string> prodSegments,
        HashSet<string> pubVariants,
        List<LeftoverItem> results,
        HashSet<string> seenPaths,
        CancellationToken ct)
    {
        var shortcutRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\Start Menu\Programs")
        };

        foreach (var root in shortcutRoots)
        {
            if (ct.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;

            try
            {
                // Check directories (e.g. "Programs\Git" or "Programs\Vendor")
                foreach (var dir in Directory.GetDirectories(root))
                {
                    if (ct.IsCancellationRequested) break;
                    var dirName = Path.GetFileName(dir);
                    if (string.IsNullOrWhiteSpace(dirName)) continue;

                    if (IsDirectAppMatch(dirName, fullVariants))
                    {
                        var full = Path.GetFullPath(dir);
                        if (!IsProtectedDirectory(full) && seenPaths.Add(full))
                        {
                            var (bytes, count) = CalculateDirectoryMetrics(full, ct);
                            results.Add(new LeftoverItem
                            {
                                Kind = LeftoverKind.Folder,
                                Path = full,
                                DisplayName = dirName,
                                SizeInBytes = bytes,
                                FormattedSize = FormatBytes(bytes),
                                Details = $"{count} shortcut item(s) in Start Menu folder"
                            });
                        }
                    }
                    else if (IsPublisherMatch(dirName, pubVariants))
                    {
                        try
                        {
                            foreach (var sub in Directory.GetDirectories(dir))
                            {
                                var subName = Path.GetFileName(sub);
                                if (IsDirectAppMatch(subName, fullVariants) || IsProductSegmentMatch(subName, prodSegments))
                                {
                                    var full = Path.GetFullPath(sub);
                                    if (!IsProtectedDirectory(full) && seenPaths.Add(full))
                                    {
                                        var (bytes, count) = CalculateDirectoryMetrics(full, ct);
                                        results.Add(new LeftoverItem
                                        {
                                            Kind = LeftoverKind.Folder,
                                            Path = full,
                                            DisplayName = $"{dirName}\\{subName}",
                                            SizeInBytes = bytes,
                                            FormattedSize = FormatBytes(bytes),
                                            Details = $"{count} shortcut item(s)"
                                        });
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }

                // Check individual .lnk files directly in the root folder
                foreach (var file in Directory.GetFiles(root, "*.lnk"))
                {
                    if (ct.IsCancellationRequested) break;
                    var fileNameNoExt = Path.GetFileNameWithoutExtension(file);
                    if (IsDirectAppMatch(fileNameNoExt, fullVariants))
                    {
                        var full = Path.GetFullPath(file);
                        if (seenPaths.Add(full))
                        {
                            long size = 0;
                            try { size = new FileInfo(full).Length; } catch { }
                            results.Add(new LeftoverItem
                            {
                                Kind = LeftoverKind.File,
                                Path = full,
                                DisplayName = Path.GetFileName(full),
                                SizeInBytes = size,
                                FormattedSize = FormatBytes(size),
                                Details = "Application shortcut"
                            });
                        }
                    }
                }
            }
            catch { }
        }
    }

    private static void ScanSoftwareRegistry(
        HashSet<string> fullVariants,
        HashSet<string> prodSegments,
        HashSet<string> pubVariants,
        List<LeftoverItem> results,
        HashSet<string> seenPaths,
        CancellationToken ct)
    {
        var targets = new (RegistryHive Hive, string SubPath, RegistryView View, string DisplayPrefix)[]
        {
            (RegistryHive.CurrentUser, "Software", RegistryView.Default, @"HKCU\Software"),
            (RegistryHive.LocalMachine, "SOFTWARE", RegistryView.Registry64, @"HKLM\SOFTWARE"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node", RegistryView.Registry32, @"HKLM\SOFTWARE\WOW6432Node")
        };

        foreach (var (hive, subPath, view, prefix) in targets)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var rootKey = baseKey.OpenSubKey(subPath);
                if (rootKey == null) continue;

                foreach (var subKeyName in rootKey.GetSubKeyNames())
                {
                    if (ct.IsCancellationRequested) break;
                    if (string.IsNullOrWhiteSpace(subKeyName)) continue;

                    // Direct application match under Software
                    if (IsDirectAppMatch(subKeyName, fullVariants))
                    {
                        var fullRegPath = $@"{prefix}\{subKeyName}";
                        if (seenPaths.Add(fullRegPath))
                        {
                            results.Add(new LeftoverItem
                            {
                                Kind = LeftoverKind.RegistryKey,
                                Path = fullRegPath,
                                DisplayName = subKeyName,
                                SizeInBytes = 0,
                                FormattedSize = "-",
                                Details = "Application configuration key"
                            });
                        }
                        continue;
                    }

                    // Publisher match under Software (e.g. Google, Mozilla)
                    if (IsPublisherMatch(subKeyName, pubVariants))
                    {
                        try
                        {
                            using var pubKey = rootKey.OpenSubKey(subKeyName);
                            if (pubKey == null) continue;

                            foreach (var childName in pubKey.GetSubKeyNames())
                            {
                                if (ct.IsCancellationRequested) break;
                                if (IsDirectAppMatch(childName, fullVariants) || IsProductSegmentMatch(childName, prodSegments))
                                {
                                    var fullRegPath = $@"{prefix}\{subKeyName}\{childName}";
                                    if (seenPaths.Add(fullRegPath))
                                    {
                                        results.Add(new LeftoverItem
                                        {
                                            Kind = LeftoverKind.RegistryKey,
                                            Path = fullRegPath,
                                            DisplayName = $@"{subKeyName}\{childName}",
                                            SizeInBytes = 0,
                                            FormattedSize = "-",
                                            Details = "Publisher application key"
                                        });
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }
    }

    private static void ScanArpRegistry(
        string appName,
        string? rawId,
        HashSet<string> fullVariants,
        List<LeftoverItem> results,
        HashSet<string> seenPaths,
        CancellationToken ct)
    {
        var arpTargets = new (RegistryHive Hive, string SubPath, RegistryView View, string DisplayPrefix)[]
        {
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", RegistryView.Registry64, @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", RegistryView.Registry32, @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", RegistryView.Default, @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")
        };

        foreach (var (hive, subPath, view, prefix) in arpTargets)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var rootKey = baseKey.OpenSubKey(subPath);
                if (rootKey == null) continue;

                foreach (var subKeyName in rootKey.GetSubKeyNames())
                {
                    if (ct.IsCancellationRequested) break;

                    bool isMatch = false;

                    // Match against raw package ID / GUID
                    if (!string.IsNullOrWhiteSpace(rawId) && string.Equals(subKeyName, rawId, StringComparison.OrdinalIgnoreCase))
                    {
                        isMatch = true;
                    }
                    else if (IsDirectAppMatch(subKeyName, fullVariants))
                    {
                        isMatch = true;
                    }
                    else
                    {
                        // Check DisplayName value inside key
                        try
                        {
                            using var appKey = rootKey.OpenSubKey(subKeyName);
                            if (appKey != null)
                            {
                                var dn = appKey.GetValue("DisplayName")?.ToString()?.Trim();
                                if (!string.IsNullOrWhiteSpace(dn) && IsDirectAppMatch(dn, fullVariants))
                                {
                                    isMatch = true;
                                }
                            }
                        }
                        catch { }
                    }

                    if (isMatch)
                    {
                        var fullRegPath = $@"{prefix}\{subKeyName}";
                        if (seenPaths.Add(fullRegPath))
                        {
                            results.Add(new LeftoverItem
                            {
                                Kind = LeftoverKind.RegistryKey,
                                Path = fullRegPath,
                                DisplayName = subKeyName,
                                SizeInBytes = 0,
                                FormattedSize = "-",
                                Details = "Windows Add/Remove Programs entry"
                            });
                        }
                    }
                }
            }
            catch { }
        }
    }

    public static async Task<CleanResult> CleanLeftoversAsync(
        IEnumerable<LeftoverItem> items,
        Action<string>? logAction = null,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            int total = 0;
            int deleted = 0;
            int failed = 0;
            long bytesFreed = 0;
            var logs = new List<string>();
            var errors = new List<string>();

            void Emit(string msg)
            {
                logs.Add(msg);
                logAction?.Invoke(msg);
            }

            var itemsList = items.Where(i => i.IsSelected).ToList();
            total = itemsList.Count;

            Emit($"[Deep Cleaner] Initiating deep clean for {total} selected item(s)...");

            foreach (var item in itemsList)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    switch (item.Kind)
                    {
                        case LeftoverKind.Folder:
                            if (Directory.Exists(item.Path))
                            {
                                if (IsProtectedDirectory(item.Path))
                                {
                                    Emit($"[Skipped Protected Directory] {item.Path}");
                                    failed++;
                                    continue;
                                }

                                RemoveReadOnlyAttributes(new DirectoryInfo(item.Path));
                                Directory.Delete(item.Path, true);
                                bytesFreed += item.SizeInBytes;
                                deleted++;
                                Emit($"[Deleted Folder] {item.Path} ({item.FormattedSize})");

                                // Clean empty parent if it was a publisher directory
                                TryCleanEmptyPublisherParent(item.Path, Emit);
                            }
                            else
                            {
                                deleted++;
                            }
                            break;

                        case LeftoverKind.File:
                            if (File.Exists(item.Path))
                            {
                                var fi = new FileInfo(item.Path);
                                if (fi.IsReadOnly) fi.IsReadOnly = false;
                                fi.Delete();
                                bytesFreed += item.SizeInBytes;
                                deleted++;
                                Emit($"[Deleted File] {item.Path}");
                            }
                            else
                            {
                                deleted++;
                            }
                            break;

                        case LeftoverKind.RegistryKey:
                            DeleteRegistryKey(item.Path, Emit);
                            deleted++;
                            break;

                        case LeftoverKind.RegistryValue:
                            DeleteRegistryValue(item.Path, Emit);
                            deleted++;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    var errMsg = $"Failed to clean {item.Path}: {ex.Message}";
                    errors.Add(errMsg);
                    Emit($"[Error] {errMsg}");
                }
            }

            var summary = $"[Deep Cleaner] Finished. Successfully cleaned {deleted} of {total} item(s) ({FormatBytes(bytesFreed)} freed).";
            if (failed > 0)
            {
                summary += $" {failed} item(s) could not be removed.";
            }
            Emit(summary);

            return new CleanResult(total, deleted, failed, bytesFreed, logs, errors);
        }, ct);
    }

    private static void RemoveReadOnlyAttributes(DirectoryInfo dir)
    {
        try
        {
            if ((dir.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
            {
                dir.Attributes &= ~FileAttributes.ReadOnly;
            }

            foreach (var file in dir.GetFiles("*", SearchOption.AllDirectories))
            {
                try
                {
                    if (file.IsReadOnly)
                    {
                        file.IsReadOnly = false;
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    private static void TryCleanEmptyPublisherParent(string deletedDirPath, Action<string> emit)
    {
        try
        {
            var parent = Path.GetDirectoryName(deletedDirPath);
            if (string.IsNullOrWhiteSpace(parent) || IsProtectedDirectory(parent)) return;

            // Only attempt if inside AppData or ProgramData
            var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roamApp = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var progData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

            bool isInsideAppData = (!string.IsNullOrWhiteSpace(localApp) && parent.StartsWith(localApp, StringComparison.OrdinalIgnoreCase)) ||
                                   (!string.IsNullOrWhiteSpace(roamApp) && parent.StartsWith(roamApp, StringComparison.OrdinalIgnoreCase)) ||
                                   (!string.IsNullOrWhiteSpace(progData) && parent.StartsWith(progData, StringComparison.OrdinalIgnoreCase));

            if (!isInsideAppData) return;

            if (Directory.Exists(parent))
            {
                var files = Directory.GetFiles(parent);
                var dirs = Directory.GetDirectories(parent);
                if (files.Length == 0 && dirs.Length == 0)
                {
                    Directory.Delete(parent, false);
                    emit($"[Deleted Empty Directory] {parent}");
                }
            }
        }
        catch { }
    }

    private static void DeleteRegistryKey(string fullPath, Action<string> emit)
    {
        var (hive, view, subPath) = ParseRegistryPath(fullPath);
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        baseKey.DeleteSubKeyTree(subPath, false);
        emit($"[Deleted Registry Key] {fullPath}");
    }

    private static void DeleteRegistryValue(string fullPath, Action<string> emit)
    {
        var lastSlash = fullPath.LastIndexOf('\\');
        if (lastSlash < 0) return;

        var keyPath = fullPath[..lastSlash];
        var valueName = fullPath[(lastSlash + 1)..];

        var (hive, view, subPath) = ParseRegistryPath(keyPath);
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var key = baseKey.OpenSubKey(subPath, true);
        if (key != null)
        {
            key.DeleteValue(valueName, false);
            emit($"[Deleted Registry Value] {fullPath}");
        }
    }

    private static (RegistryHive Hive, RegistryView View, string SubPath) ParseRegistryPath(string path)
    {
        RegistryHive hive = RegistryHive.CurrentUser;
        string relative = path;

        if (path.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(@"HKEY_CURRENT_USER\", StringComparison.OrdinalIgnoreCase))
        {
            hive = RegistryHive.CurrentUser;
            relative = path[(path.IndexOf('\\') + 1)..];
        }
        else if (path.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase) ||
                 path.StartsWith(@"HKEY_LOCAL_MACHINE\", StringComparison.OrdinalIgnoreCase))
        {
            hive = RegistryHive.LocalMachine;
            relative = path[(path.IndexOf('\\') + 1)..];
        }

        RegistryView view = RegistryView.Default;
        if (hive == RegistryHive.LocalMachine)
        {
            view = relative.Contains("WOW6432Node", StringComparison.OrdinalIgnoreCase)
                ? RegistryView.Registry32
                : RegistryView.Registry64;
        }

        return (hive, view, relative);
    }

    public static bool IsProtectedDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;

        string normalized;
        try
        {
            normalized = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return true;
        }

        // Never touch root drive (e.g. C:\)
        var root = Path.GetPathRoot(normalized);
        if (string.Equals(root?.TrimEnd('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase) || normalized.Length <= 3)
        {
            return true;
        }

        // Must be at least 2 levels deep from root (e.g. C:\Program Files\Vendor is level 2)
        var depth = normalized.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).Length;
        if (depth < 2)
        {
            return true;
        }

        // Check against known system directories
        if (ProtectedBasePaths.Contains(normalized))
        {
            return true;
        }

        // Check if inside Windows directory
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(winDir) && normalized.StartsWith(winDir, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static (long Bytes, int FileCount) CalculateDirectoryMetrics(string path, CancellationToken ct)
    {
        long bytes = 0;
        int count = 0;

        try
        {
            var di = new DirectoryInfo(path);
            var queue = new Queue<DirectoryInfo>();
            queue.Enqueue(di);

            while (queue.Count > 0)
            {
                if (ct.IsCancellationRequested) break;
                var current = queue.Dequeue();

                try
                {
                    foreach (var file in current.GetFiles())
                    {
                        if (ct.IsCancellationRequested) break;
                        bytes += file.Length;
                        count++;
                    }

                    foreach (var sub in current.GetDirectories())
                    {
                        if (ct.IsCancellationRequested) break;
                        queue.Enqueue(sub);
                    }
                }
                catch
                {
                    // Skip files with security or IO restrictions
                }
            }
        }
        catch
        {
            // Skip top level if directory cannot be opened
        }

        return (bytes, count);
    }

    private static (HashSet<string> FullVariants, HashSet<string> ProdSegments, HashSet<string> PubVariants, string? RawId) ExtractSearchTerms(PackageItem item)
    {
        var fullVariants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prodSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pubVariants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Clean App Name
        if (!string.IsNullOrWhiteSpace(item.Name))
        {
            var name = item.Name.Trim();
            fullVariants.Add(name);

            // Strip parentheses (e.g. (64-bit), (x64), (x86), (en-US))
            var cleanName = Regex.Replace(name, @"\(.*?\)", "").Trim();
            if (!string.IsNullOrWhiteSpace(cleanName))
            {
                fullVariants.Add(cleanName);
                fullVariants.Add(cleanName.Replace(" ", ""));
            }

            // Word parts
            var words = cleanName.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 1)
            {
                var lastWord = words[^1];
                if (lastWord.Length >= 3 && !IsGenericTerm(lastWord))
                {
                    prodSegments.Add(lastWord);
                }
            }
        }

        // 2. Publisher
        if (!string.IsNullOrWhiteSpace(item.Publisher))
        {
            var pub = item.Publisher.Trim();
            pubVariants.Add(pub);

            var cleanPub = Regex.Replace(pub, @"(?i)\b(llc|inc|corp|corporation|ltd|technologies|software|development|community)\b", "").Trim();
            if (!string.IsNullOrWhiteSpace(cleanPub))
            {
                pubVariants.Add(cleanPub);
            }
        }

        // 3. Package ID
        string? rawId = null;
        if (!string.IsNullOrWhiteSpace(item.Id))
        {
            rawId = item.Id
                .Replace(@"ARP\Machine\X64\", "", StringComparison.OrdinalIgnoreCase)
                .Replace(@"ARP\Machine\X86\", "", StringComparison.OrdinalIgnoreCase)
                .Replace(@"ARP\User\", "", StringComparison.OrdinalIgnoreCase)
                .Trim();

            if (item.Id.Contains('.'))
            {
                var idParts = item.Id.Split('.', StringSplitOptions.RemoveEmptyEntries);
                if (idParts.Length >= 2)
                {
                    var idPub = idParts[0];
                    if (idPub.Length >= 3 && !idPub.Equals("ARP", StringComparison.OrdinalIgnoreCase) && !idPub.Equals("MSIX", StringComparison.OrdinalIgnoreCase))
                    {
                        pubVariants.Add(idPub);
                    }

                    var idProd = idParts[^1];
                    if (idProd.Length >= 3 && !IsGenericTerm(idProd))
                    {
                        prodSegments.Add(idProd);
                        fullVariants.Add(idProd);
                    }
                }
            }
        }

        return (fullVariants, prodSegments, pubVariants, rawId);
    }

    private static bool IsGenericTerm(string term)
    {
        var generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Desktop", "Client", "Tool", "Utility", "Application", "App", "Suite", "Edition", "Studio", "Community", "Enterprise", "Professional", "Standard", "Setup", "Installer"
        };
        return generic.Contains(term);
    }

    private static bool IsDirectAppMatch(string candidate, HashSet<string> fullVariants)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;

        foreach (var variant in fullVariants)
        {
            if (string.Equals(candidate, variant, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // For terms <= 4 chars (e.g. "Git", "Go", "VLC"), ONLY allow exact match
            if (variant.Length <= 4)
            {
                continue;
            }

            // Space-less exact match (e.g. "GoogleChrome" == "Google Chrome")
            if (string.Equals(candidate.Replace(" ", ""), variant.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Word-boundary / hyphen / underscore prefix match (e.g. "Google Chrome - Beta" starts with "Google Chrome")
            if (candidate.StartsWith(variant + " ", StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith(variant + "-", StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith(variant + "_", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPublisherMatch(string candidate, HashSet<string> pubVariants)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;

        foreach (var pub in pubVariants)
        {
            if (string.Equals(candidate, pub, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (pub.Length > 4 && (candidate.StartsWith(pub + " ", StringComparison.OrdinalIgnoreCase) || candidate.StartsWith(pub + "-", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsProductSegmentMatch(string candidate, HashSet<string> prodSegments)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;

        foreach (var prod in prodSegments)
        {
            if (string.Equals(candidate, prod, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (prod.Length > 4 && (candidate.StartsWith(prod + " ", StringComparison.OrdinalIgnoreCase) || candidate.StartsWith(prod + "-", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1 && counter < suffixes.Length - 1)
        {
            number /= 1024;
            counter++;
        }
        return $"{number:n1} {suffixes[counter]}";
    }
}
