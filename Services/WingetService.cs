using System.Diagnostics;
using System.IO;
using System.Text;
using WingetGui.Models;

namespace WingetGui.Services;

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

        // Query registry in parallel / background
        var registryTask = Task.Run(RegistryService.GetInstalledRegistryApps, ct);

        var (exitCode, stdout, _) = await RunProcessAsync("winget", "list --accept-source-agreements", ct);
        if (exitCode != 0 && string.IsNullOrWhiteSpace(stdout))
        {
            return items;
        }

        var registryMap = await registryTask;
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
                IsInstalled = true
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

    public async Task<List<PackageItem>> GetAvailableUpgradesAsync(CancellationToken ct = default)
    {
        var items = new List<PackageItem>();

        var registryTask = Task.Run(RegistryService.GetInstalledRegistryApps, ct);
        var (exitCode, stdout, _) = await RunProcessAsync("winget", "upgrade --include-unknown --accept-source-agreements", ct);
        if (exitCode != 0 && string.IsNullOrWhiteSpace(stdout))
        {
            return items;
        }

        var registryMap = await registryTask;
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
                IsInstalled = true
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

    public async Task<int> StreamCommandAsync(string commandArgs, Action<string> onOutputLine, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "winget",
            Arguments = commandArgs,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                onOutputLine(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                onOutputLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(string filename, string args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = filename,
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
