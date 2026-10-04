using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using WingetGui.Models;

namespace WingetGui.Services;

public record DownloadProgressInfo(
    string State,
    long DownloadedBytes,
    long TotalBytes,
    double Percentage,
    string FormattedProgress
);

public class WingetService
{
    public async Task<string?> GetWingetVersionAsync()
    {
        try
        {
            var (exitCode, stdout, _) = await RunProcessAsync("winget", "--version");
            if (exitCode == 0)
            {
                return stdout.Trim();
            }
        }
        catch
        {
            // Winget not found or failed
        }
        return null;
    }

    public async Task<List<PackageItem>> GetInstalledPackagesAsync(CancellationToken ct = default)
    {
        var items = new List<PackageItem>();

        // Query registry and pins in parallel / background
        var registryTask = Task.Run(RegistryService.GetInstalledRegistryApps, ct);
        var pinnedTask = GetPinnedPackageIdsAsync(ct);

        var (exitCode, stdout, _) = await RunProcessAsync("winget", "list --accept-source-agreements", ct);
        if (exitCode != 0 && string.IsNullOrWhiteSpace(stdout))
        {
            return items;
        }

        var registryMap = await registryTask;
        var pinnedIds = await pinnedTask;
        var lines = stdout.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        if (lines.Length < 3) return items;

        int headerLineIdx = -1;
        for (int i = 0; i < Math.Min(lines.Length, 10); i++)
        {
            if (lines[i].Contains("Name") && lines[i].Contains("Id"))
            {
                headerLineIdx = i;
                break;
            }
        }

        if (headerLineIdx == -1) return items;

        var header = lines[headerLineIdx];
        int idIdx = header.IndexOf("Id", StringComparison.Ordinal);
        int verIdx = header.IndexOf("Version", StringComparison.Ordinal);
        int availIdx = header.IndexOf("Available", StringComparison.Ordinal);
        int srcIdx = header.IndexOf("Source", StringComparison.Ordinal);

        if (idIdx <= 0 || verIdx <= 0) return items;

        for (int i = headerLineIdx + 2; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("---") || line.StartsWith("<additional"))
            {
                continue;
            }

            if (line.Length < idIdx) continue;

            string name = line[..Math.Min(idIdx, line.Length)].Trim();
            string id = (line.Length > idIdx)
                ? (line.Length >= verIdx ? line[idIdx..verIdx].Trim() : line[idIdx..].Trim())
                : string.Empty;

            string version = string.Empty;
            if (line.Length > verIdx)
            {
                if (availIdx > verIdx && line.Length >= availIdx)
                {
                    version = line[verIdx..availIdx].Trim();
                }
                else if (srcIdx > verIdx && line.Length >= srcIdx)
                {
                    version = line[verIdx..srcIdx].Trim();
                }
                else
                {
                    version = line[verIdx..].Trim();
                }
            }

            string available = string.Empty;
            if (availIdx > 0 && line.Length > availIdx)
            {
                if (srcIdx > availIdx && line.Length >= srcIdx)
                {
                    available = line[availIdx..srcIdx].Trim();
                }
                else
                {
                    available = line[availIdx..].Trim();
                }
            }

            string source = string.Empty;
            if (srcIdx > 0 && line.Length > srcIdx)
            {
                source = line[srcIdx..].Trim();
            }

            if (string.IsNullOrWhiteSpace(name)) continue;

            var item = new PackageItem
            {
                Name = name,
                Id = id,
                Version = version,
                AvailableVersion = available,
                Source = source,
                HasUpdate = !string.IsNullOrEmpty(available),
                IsInstalled = true,
                IsPinned = pinnedIds.Contains(id)
            };

            // Enrich with registry metadata
            var regInfo = RegistryService.FindApp(registryMap, name, id);
            if (regInfo != null)
            {
                item.EstimatedSizeInKb = regInfo.EstimatedSizeKb;
                item.FormattedSize = regInfo.FormattedSize ?? string.Empty;
                item.InstallDate = regInfo.FormattedInstallDate ?? string.Empty;
                item.Publisher = regInfo.Publisher ?? string.Empty;
                item.InstallLocation = regInfo.InstallLocation ?? string.Empty;
                item.UninstallString = regInfo.UninstallString ?? string.Empty;
                item.Architecture = regInfo.Architecture;
                item.IconPath = regInfo.DisplayIcon ?? string.Empty;
            }
            else
            {
                // Fallback architecture detection from name
                if (name.Contains("(32-bit)", StringComparison.OrdinalIgnoreCase) || name.Contains("x86", StringComparison.OrdinalIgnoreCase))
                {
                    item.Architecture = "32-bit";
                }
                else if (name.Contains("(x64)", StringComparison.OrdinalIgnoreCase) || name.Contains("(64-bit)", StringComparison.OrdinalIgnoreCase))
                {
                    item.Architecture = "64-bit";
                }
            }

            // Load icon
            item.Icon = IconService.GetIcon(string.IsNullOrEmpty(item.IconPath) ? item.InstallLocation : item.IconPath);

            items.Add(item);
        }

        return items;
    }

    public async Task<HashSet<string>> GetPinnedPackageIdsAsync(CancellationToken ct = default)
    {
        var pinnedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var (exitCode, stdout, _) = await RunProcessAsync("winget", "pin list", ct);
            if (exitCode != 0 && string.IsNullOrWhiteSpace(stdout))
            {
                return pinnedIds;
            }

            var lines = stdout.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            if (lines.Length < 3) return pinnedIds;

            int headerLineIdx = -1;
            for (int i = 0; i < Math.Min(lines.Length, 10); i++)
            {
                if (lines[i].Contains("Name") && lines[i].Contains("Id"))
                {
                    headerLineIdx = i;
                    break;
                }
            }

            if (headerLineIdx == -1) return pinnedIds;

            var header = lines[headerLineIdx];
            int idIdx = header.IndexOf("Id", StringComparison.Ordinal);
            int verIdx = header.IndexOf("Version", StringComparison.Ordinal);
            if (idIdx <= 0) return pinnedIds;

            for (int i = headerLineIdx + 2; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("---"))
                {
                    continue;
                }

                if (line.Length <= idIdx) continue;

                string id = (verIdx > idIdx && line.Length >= verIdx)
                    ? line[idIdx..verIdx].Trim()
                    : line[idIdx..].Trim();

                if (!string.IsNullOrWhiteSpace(id))
                {
                    pinnedIds.Add(id);
                }
            }
        }
        catch
        {
            // Ignore pin list failures
        }
        return pinnedIds;
    }

    public async Task<List<PackageItem>> GetAvailableUpgradesAsync(CancellationToken ct = default)
    {
        var items = new List<PackageItem>();

        var registryTask = Task.Run(RegistryService.GetInstalledRegistryApps, ct);
        var pinnedTask = GetPinnedPackageIdsAsync(ct);

        var (exitCode, stdout, _) = await RunProcessAsync("winget", "upgrade --include-unknown --include-pinned --accept-source-agreements", ct);
        if (exitCode != 0 && string.IsNullOrWhiteSpace(stdout))
        {
            var fallback = await RunProcessAsync("winget", "upgrade --include-unknown --accept-source-agreements", ct);
            exitCode = fallback.ExitCode;
            stdout = fallback.StdOut;
        }

        if (exitCode != 0 && string.IsNullOrWhiteSpace(stdout))
        {
            return items;
        }

        var registryMap = await registryTask;
        var pinnedIds = await pinnedTask;
        var lines = stdout.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        if (lines.Length < 3) return items;

        int headerLineIdx = -1;
        for (int i = 0; i < Math.Min(lines.Length, 10); i++)
        {
            if (lines[i].Contains("Name") && lines[i].Contains("Id"))
            {
                headerLineIdx = i;
                break;
            }
        }

        if (headerLineIdx == -1) return items;

        var header = lines[headerLineIdx];
        int idIdx = header.IndexOf("Id", StringComparison.Ordinal);
        int verIdx = header.IndexOf("Version", StringComparison.Ordinal);
        int availIdx = header.IndexOf("Available", StringComparison.Ordinal);
        int srcIdx = header.IndexOf("Source", StringComparison.Ordinal);

        if (idIdx <= 0 || verIdx <= 0) return items;

        for (int i = headerLineIdx + 2; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("---") || line.StartsWith("<additional"))
            {
                continue;
            }

            if (line.Length < idIdx) continue;

            string name = line[..Math.Min(idIdx, line.Length)].Trim();
            string id = (line.Length > idIdx)
                ? (line.Length >= verIdx ? line[idIdx..verIdx].Trim() : line[idIdx..].Trim())
                : string.Empty;

            string version = string.Empty;
            if (line.Length > verIdx)
            {
                if (availIdx > verIdx && line.Length >= availIdx)
                {
                    version = line[verIdx..availIdx].Trim();
                }
                else if (srcIdx > verIdx && line.Length >= srcIdx)
                {
                    version = line[verIdx..srcIdx].Trim();
                }
                else
                {
                    version = line[verIdx..].Trim();
                }
            }

            string available = string.Empty;
            if (availIdx > 0 && line.Length > availIdx)
            {
                if (srcIdx > availIdx && line.Length >= srcIdx)
                {
                    available = line[availIdx..srcIdx].Trim();
                }
                else
                {
                    available = line[availIdx..].Trim();
                }
            }

            string source = string.Empty;
            if (srcIdx > 0 && line.Length > srcIdx)
            {
                source = line[srcIdx..].Trim();
            }

            if (string.IsNullOrWhiteSpace(name)) continue;

            var item = new PackageItem
            {
                Name = name,
                Id = id,
                Version = version,
                AvailableVersion = available,
                Source = source,
                HasUpdate = true,
                IsInstalled = true,
                IsPinned = pinnedIds.Contains(id)
            };

            var regInfo = RegistryService.FindApp(registryMap, name, id);
            if (regInfo != null)
            {
                item.EstimatedSizeInKb = regInfo.EstimatedSizeKb;
                item.FormattedSize = regInfo.FormattedSize ?? string.Empty;
                item.InstallDate = regInfo.FormattedInstallDate ?? string.Empty;
                item.Publisher = regInfo.Publisher ?? string.Empty;
                item.InstallLocation = regInfo.InstallLocation ?? string.Empty;
                item.UninstallString = regInfo.UninstallString ?? string.Empty;
                item.Architecture = regInfo.Architecture;
                item.IconPath = regInfo.DisplayIcon ?? string.Empty;
            }

            item.Icon = IconService.GetIcon(string.IsNullOrEmpty(item.IconPath) ? item.InstallLocation : item.IconPath);

            items.Add(item);
        }

        return items;
    }

    public async Task<List<PackageItem>> SearchPackagesAsync(string query, int count = 30, CancellationToken ct = default)
    {
        var items = new List<PackageItem>();
        if (string.IsNullOrWhiteSpace(query)) return items;

        var (exitCode, stdout, _) = await RunProcessAsync("winget", $"search \"{query}\" -n {count} --accept-source-agreements", ct);
        if (exitCode != 0 && string.IsNullOrWhiteSpace(stdout))
        {
            return items;
        }

        var lines = stdout.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        if (lines.Length < 3) return items;

        int headerLineIdx = -1;
        for (int i = 0; i < Math.Min(lines.Length, 10); i++)
        {
            if (lines[i].Contains("Name") && lines[i].Contains("Id"))
            {
                headerLineIdx = i;
                break;
            }
        }

        if (headerLineIdx == -1) return items;

        var header = lines[headerLineIdx];
        int idIdx = header.IndexOf("Id", StringComparison.Ordinal);
        int verIdx = header.IndexOf("Version", StringComparison.Ordinal);
        int matchIdx = header.IndexOf("Match", StringComparison.Ordinal);
        int srcIdx = header.IndexOf("Source", StringComparison.Ordinal);

        if (idIdx <= 0 || verIdx <= 0) return items;

        for (int i = headerLineIdx + 2; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("---"))
            {
                continue;
            }

            if (line.Length < idIdx) continue;

            string name = line[..Math.Min(idIdx, line.Length)].Trim();
            string id = (line.Length > idIdx)
                ? (line.Length >= verIdx ? line[idIdx..verIdx].Trim() : line[idIdx..].Trim())
                : string.Empty;

            string version = string.Empty;
            if (line.Length > verIdx)
            {
                int endIdx = matchIdx > verIdx ? matchIdx : (srcIdx > verIdx ? srcIdx : line.Length);
                version = line[verIdx..Math.Min(endIdx, line.Length)].Trim();
            }

            string source = string.Empty;
            if (srcIdx > 0 && line.Length > srcIdx)
            {
                source = line[srcIdx..].Trim();
            }

            if (string.IsNullOrWhiteSpace(name)) continue;

            var item = new PackageItem
            {
                Name = name,
                Id = id,
                Version = version,
                Source = source,
                HasUpdate = false,
                IsInstalled = false,
                Icon = IconService.GetDefaultIcon()
            };

            items.Add(item);
        }

        return items;
    }

    public async Task<string> GetPackageDetailsAsync(string packageId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(packageId)) return string.Empty;

        var (_, stdout, stderr) = await RunProcessAsync("winget", $"show \"{packageId}\" --exact --accept-source-agreements", ct);
        return string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
    }

    public static string GetWingetPath()
    {
        var localAppWinget = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(localAppWinget))
        {
            return localAppWinget;
        }
        return "winget";
    }

    public Task<int> StreamCommandAsync(string commandArgs, Action<string> onOutputLine, CancellationToken ct = default)
    {
        return StreamCommandAsync(commandArgs, onOutputLine, null, ct);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSizeW(
        [In, MarshalAs(UnmanagedType.LPWStr)] string lpFileName,
        [Out] out uint lpFileSizeHigh);

    private static long GetPhysicalFileSize(string filePath)
    {
        try
        {
            uint high = 0;
            uint low = GetCompressedFileSizeW(filePath, out high);
            const uint INVALID_FILE_SIZE = 0xFFFFFFFF;
            if (low == INVALID_FILE_SIZE && Marshal.GetLastWin32Error() != 0)
            {
                return -1;
            }
            return ((long)high << 32) | low;
        }
        catch
        {
            return -1;
        }
    }

    public async Task<int> StreamCommandAsync(
        string commandArgs,
        Action<string> onOutputLine,
        Action<DownloadProgressInfo>? onProgress,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = GetWingetPath(),
            Arguments = commandArgs,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? monitorTask = null;
        long totalBytes = 0;
        var operationStartTime = DateTime.UtcNow.AddSeconds(-5);

        var tempRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp");
        var wingetTempPath = Path.Combine(tempRoot, "WinGet");

        void TryQueryContentLength(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("WinGet");

                    // 1. Try HEAD request first
                    try
                    {
                        using var headReq = new HttpRequestMessage(HttpMethod.Head, url);
                        using var headResp = await client.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead, monitorCts.Token);
                        if (headResp.IsSuccessStatusCode && headResp.Content.Headers.ContentLength.HasValue && headResp.Content.Headers.ContentLength.Value > 0)
                        {
                            Interlocked.Exchange(ref totalBytes, headResp.Content.Headers.ContentLength.Value);
                            return;
                        }
                    }
                    catch { }

                    // 2. Fallback to GET Range: bytes=0-0 (downloads only 1 single byte)
                    try
                    {
                        using var getReq = new HttpRequestMessage(HttpMethod.Get, url);
                        getReq.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                        using var getResp = await client.SendAsync(getReq, HttpCompletionOption.ResponseHeadersRead, monitorCts.Token);
                        if (getResp.Content.Headers.ContentRange?.Length is long rangeLen && rangeLen > 0)
                        {
                            Interlocked.Exchange(ref totalBytes, rangeLen);
                            return;
                        }
                        if (getResp.Content.Headers.ContentLength.HasValue && getResp.Content.Headers.ContentLength.Value > 0)
                        {
                            Interlocked.Exchange(ref totalBytes, getResp.Content.Headers.ContentLength.Value);
                            return;
                        }
                    }
                    catch { }
                }
                catch { }
            }, monitorCts.Token);
        }

        FileInfo? FindBestActiveFile()
        {
            FileInfo? best = null;
            long bestSize = -1;
            DateTime bestTime = operationStartTime;

            // 1. Check WinGet temp directory (strictly excluding \cache\)
            if (Directory.Exists(wingetTempPath))
            {
                try
                {
                    var di = new DirectoryInfo(wingetTempPath);
                    foreach (var file in di.GetFiles("*", SearchOption.AllDirectories))
                    {
                        if (file.FullName.Contains(@"\cache\", StringComparison.OrdinalIgnoreCase) ||
                            file.FullName.EndsWith(@"\cache", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var ext = file.Extension;
                        if (ext.Equals(".yaml", StringComparison.OrdinalIgnoreCase) ||
                            ext.Equals(".mszyml", StringComparison.OrdinalIgnoreCase) ||
                            ext.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
                            ext.Equals(".log", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (file.LastWriteTimeUtc >= operationStartTime)
                        {
                            long physical = GetPhysicalFileSize(file.FullName);
                            long size = physical >= 0 ? physical : file.Length;
                            if ((size > 65536 || file.Length > 65536) && (size > bestSize || file.LastWriteTimeUtc > bestTime))
                            {
                                best = file;
                                bestSize = size;
                                bestTime = file.LastWriteTimeUtc;
                            }
                        }
                    }
                }
                catch { }
            }

            // 2. Check top-level %LOCALAPPDATA%\Temp (*.tmp, *.exe, *.msi, *.msix, *.zip)
            if (Directory.Exists(tempRoot))
            {
                try
                {
                    var di = new DirectoryInfo(tempRoot);
                    foreach (var file in di.GetFiles("*.*", SearchOption.TopDirectoryOnly))
                    {
                        var ext = file.Extension;
                        if (!ext.Equals(".tmp", StringComparison.OrdinalIgnoreCase) &&
                            !ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
                            !ext.Equals(".msi", StringComparison.OrdinalIgnoreCase) &&
                            !ext.Equals(".msix", StringComparison.OrdinalIgnoreCase) &&
                            !ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (file.LastWriteTimeUtc >= operationStartTime)
                        {
                            long physical = GetPhysicalFileSize(file.FullName);
                            long size = physical >= 0 ? physical : file.Length;
                            if ((size > 65536 || file.Length > 65536) && (size > bestSize || file.LastWriteTimeUtc > bestTime))
                            {
                                best = file;
                                bestSize = size;
                                bestTime = file.LastWriteTimeUtc;
                            }
                        }
                    }
                }
                catch { }
            }

            return best;
        }

        void StartDownloadMonitor()
        {
            if (monitorTask != null) return;

            monitorTask = Task.Run(async () =>
            {
                double lastReportedMb = -1;
                int lastReportedPct = -1;
                long lastReportedTotal = -1;
                string? trackedFilePath = null;
                int ticksSinceScan = 0;

                while (!monitorCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        FileInfo? activeFile = null;
                        ticksSinceScan++;

                        if (!string.IsNullOrEmpty(trackedFilePath) && File.Exists(trackedFilePath))
                        {
                            activeFile = new FileInfo(trackedFilePath);
                            activeFile.Refresh();
                            long currentSize = activeFile.Length;

                            // Periodically check if a larger active installer candidate exists
                            if (ticksSinceScan >= 4 || currentSize <= 65536)
                            {
                                ticksSinceScan = 0;
                                var candidate = FindBestActiveFile();
                                if (candidate != null && candidate.FullName != trackedFilePath)
                                {
                                    if (candidate.Length > currentSize)
                                    {
                                        activeFile = candidate;
                                        trackedFilePath = activeFile.FullName;
                                    }
                                }
                            }
                        }
                        else
                        {
                            activeFile = FindBestActiveFile();
                            if (activeFile != null)
                            {
                                trackedFilePath = activeFile.FullName;
                            }
                        }

                        long currentDownloaded = 0;
                        long effectiveTotal = Interlocked.Read(ref totalBytes);

                        if (activeFile != null && File.Exists(activeFile.FullName))
                        {
                            activeFile.Refresh();
                            long logical = activeFile.Length;
                            long physical = GetPhysicalFileSize(activeFile.FullName);

                            if (physical >= 0 && logical > physical)
                            {
                                // Sparse pre-allocated file (DeliveryOptimization / BITS)
                                currentDownloaded = physical;
                                effectiveTotal = logical;
                            }
                            else if (physical >= 0)
                            {
                                currentDownloaded = logical;
                                if (effectiveTotal <= 0 && Interlocked.Read(ref totalBytes) > 0)
                                {
                                    effectiveTotal = Interlocked.Read(ref totalBytes);
                                }
                            }
                            else
                            {
                                currentDownloaded = logical;
                            }
                        }

                        if (effectiveTotal > 0 || currentDownloaded > 0)
                        {
                            var currMb = (double)currentDownloaded / (1024.0 * 1024.0);
                            double percentage = 0;
                            if (effectiveTotal > 0 && effectiveTotal >= currentDownloaded)
                            {
                                percentage = (currentDownloaded * 100.0) / effectiveTotal;
                                if (percentage >= 99.0) percentage = 99.0;
                            }

                            int intPct = (int)Math.Floor(percentage);

                            if (Math.Abs(currMb - lastReportedMb) >= 0.2 || intPct != lastReportedPct || effectiveTotal != lastReportedTotal)
                            {
                                lastReportedMb = currMb;
                                lastReportedPct = intPct;
                                lastReportedTotal = effectiveTotal;

                                string progressText;
                                if (effectiveTotal > 0)
                                {
                                    double totMb = (double)effectiveTotal / (1024.0 * 1024.0);
                                    progressText = $"Downloading: {currMb:0.#} MB / {totMb:0.#} MB ({intPct}%)";
                                }
                                else
                                {
                                    progressText = $"Downloading: {currMb:0.#} MB";
                                }

                                onProgress?.Invoke(new DownloadProgressInfo(
                                    "Downloading",
                                    currentDownloaded,
                                    effectiveTotal,
                                    percentage,
                                    progressText));
                            }
                        }
                    }
                    catch { }

                    await Task.Delay(250, monitorCts.Token);
                }
            }, monitorCts.Token);
        }

        string? pendingUrl = null;

        void HandleLine(string line)
        {
            onOutputLine(line);

            if (onProgress == null) return;

            var lower = line.ToLowerInvariant();

            // Detect wrapped URLs across consecutive lines
            if (pendingUrl != null)
            {
                var trimmed = line.Trim();
                if (!trimmed.Contains(' ') && (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                                               trimmed.EndsWith(".msix", StringComparison.OrdinalIgnoreCase) ||
                                               trimmed.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ||
                                               trimmed.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                                               trimmed.Contains('.')))
                {
                    var fullUrl = pendingUrl + trimmed;
                    pendingUrl = null;
                    TryQueryContentLength(fullUrl);
                }
                else
                {
                    pendingUrl = null;
                }
            }

            if (lower.Contains("downloading") || lower.Contains("download"))
            {
                var match = Regex.Match(line, @"https?://[^\s""']+");
                if (match.Success)
                {
                    var foundUrl = match.Value;
                    if (foundUrl.EndsWith("/") || (!foundUrl.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                                                   !foundUrl.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) &&
                                                   !foundUrl.EndsWith(".msix", StringComparison.OrdinalIgnoreCase) &&
                                                   !foundUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                    {
                        pendingUrl = foundUrl;
                    }
                    TryQueryContentLength(foundUrl);
                }
                StartDownloadMonitor();
            }
            else if (lower.Contains("verified") || lower.Contains("installer hash") || lower.Contains("download completed"))
            {
                monitorCts.Cancel();
                onProgress(new DownloadProgressInfo("Verifying", totalBytes, totalBytes, 100.0, "Verifying installer hash..."));
            }
            else if (lower.Contains("starting package install") || lower.Contains("installing...") || lower.Contains("installing "))
            {
                monitorCts.Cancel();
                onProgress(new DownloadProgressInfo("Installing", 0, 0, 0.0, "Installing package..."));
            }
        }

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) HandleLine(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) HandleLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct);

        monitorCts.Cancel();
        try { if (monitorTask != null) await monitorTask; } catch { }

        onProgress?.Invoke(new DownloadProgressInfo("Completed", 0, 0, 100.0, string.Empty));
        return process.ExitCode;
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(string filename, string args, CancellationToken ct = default)
    {
        var targetFile = filename.Equals("winget", StringComparison.OrdinalIgnoreCase)
            ? GetWingetPath()
            : filename;

        var psi = new ProcessStartInfo
        {
            FileName = targetFile,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi };
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) stdoutBuilder.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) stderrBuilder.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct);

        return (process.ExitCode, stdoutBuilder.ToString(), stderrBuilder.ToString());
    }
}
