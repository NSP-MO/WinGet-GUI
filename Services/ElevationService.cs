using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using WingetGui.Models;

namespace WingetGui.Services;

public static class ElevationService
{
    public static bool IsRunningAsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public static async Task RunElevatedBatchUpgradeAsync(
        List<PackageItem> items,
        Action<string> onOutputLine,
        CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string token = Guid.NewGuid().ToString("N");

        var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exePath))
        {
            listener.Stop();
            throw new InvalidOperationException("Could not determine application executable path.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"--elevated-worker {port} {token}",
            UseShellExecute = true,
            Verb = "runas"
        };

        Process? workerProcess = null;
        try
        {
            workerProcess = Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            listener.Stop();
            throw new OperationCanceledException("Administrator elevation was canceled by user.", ex);
        }
        catch
        {
            listener.Stop();
            throw;
        }

        if (workerProcess == null)
        {
            listener.Stop();
            throw new InvalidOperationException("Failed to launch elevated worker process.");
        }

        using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        acceptCts.CancelAfter(TimeSpan.FromSeconds(25));

        TcpClient client;
        try
        {
            client = await listener.AcceptTcpClientAsync(acceptCts.Token);
        }
        catch
        {
            listener.Stop();
            if (!workerProcess.HasExited)
            {
                try { workerProcess.Kill(true); } catch { }
            }
            throw;
        }
        finally
        {
            listener.Stop();
        }

        using (client)
        using (var networkStream = client.GetStream())
        using (var reader = new StreamReader(networkStream, Utf8NoBom))
        using (var writer = new StreamWriter(networkStream, Utf8NoBom) { AutoFlush = true })
        {
            // Verify handshake
            var authMsg = await reader.ReadLineAsync(ct);
            authMsg = authMsg?.TrimStart('\uFEFF');
            if (authMsg != $"AUTH {token}")
            {
                throw new InvalidOperationException("Authentication failed for elevated worker.");
            }
            await writer.WriteLineAsync("OK");

            // Send job configuration
            string wingetPath = WingetService.GetWingetPath();
            await writer.WriteLineAsync($"WINGET_PATH {wingetPath}");
            await writer.WriteLineAsync($"COUNT {items.Count}");
            foreach (var item in items)
            {
                string targetVer = !string.IsNullOrWhiteSpace(item.AvailableVersion) ? item.AvailableVersion : "latest";
                await writer.WriteLineAsync($"ITEM\t{item.Id}\t{item.Name}\t{targetVer}\t{item.IsPinned}");
            }
            await writer.WriteLineAsync("EXECUTE");

            // Wire up cancellation
            using var cancelReg = ct.Register(() =>
            {
                try
                {
                    writer.WriteLine("CANCEL");
                }
                catch { }
            });

            // Read output stream from worker
            while (true)
            {
                var line = await reader.ReadLineAsync();
                if (line == null || line == "COMPLETE")
                {
                    break;
                }

                if (line.StartsWith("OUT\t"))
                {
                    onOutputLine(line.Substring(4));
                }
                else if (line.StartsWith("STEP\t"))
                {
                    var parts = line.Split('\t');
                    if (parts.Length >= 6)
                    {
                        onOutputLine("--------------------------------------------------");
                        onOutputLine($"[{parts[1]}/{parts[2]}] Upgrading {parts[3]} ({parts[4]}) to {parts[5]}...");
                        onOutputLine("--------------------------------------------------");
                    }
                }
                else if (line.StartsWith("STEP_DONE\t"))
                {
                    var parts = line.Split('\t');
                    int code = parts.Length > 1 && int.TryParse(parts[1], out int c) ? c : 0;
                    string pkgName = parts.Length > 2 ? parts[2] : "Package";
                    if (code == 0)
                    {
                        onOutputLine($"\n[Success] {pkgName} upgraded successfully.\n");
                    }
                    else
                    {
                        onOutputLine($"\n[Failed] {pkgName} exited with code: {code}\n");
                    }
                }
                else if (line.StartsWith("SUMMARY\t"))
                {
                    var parts = line.Split('\t');
                    string succ = parts.Length > 1 ? parts[1] : "0";
                    string fail = parts.Length > 2 ? parts[2] : "0";
                    onOutputLine("==================================================");
                    onOutputLine($"Batch upgrade completed: {succ} succeeded, {fail} failed.");
                    onOutputLine("==================================================");
                }
            }
        }

        try
        {
            await workerProcess.WaitForExitAsync(CancellationToken.None);
        }
        catch { }
    }
}

public static class ElevatedWorkerService
{
    private record JobItem(string Id, string Name, string TargetVersion, bool IsPinned = false);

    public static async Task RunWorkerAsync(string[] args)
    {
        if (args.Length < 3) return;
        if (!int.TryParse(args[1], out int port)) return;
        string token = args[2];

        try
        {
            var utf8NoBom = new UTF8Encoding(false);
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using var networkStream = client.GetStream();
            using var reader = new StreamReader(networkStream, utf8NoBom);
            using var writer = new StreamWriter(networkStream, utf8NoBom) { AutoFlush = true };

            // 1. Handshake
            await writer.WriteLineAsync($"AUTH {token}");
            var okMsg = await reader.ReadLineAsync();
            okMsg = okMsg?.TrimStart('\uFEFF');
            if (okMsg != "OK") return;

            // 2. Read Job Config
            string wingetPath = "winget";
            int count = 0;
            var items = new List<JobItem>();

            while (true)
            {
                var line = await reader.ReadLineAsync();
                if (line == null || line == "EXECUTE") break;

                if (line.StartsWith("WINGET_PATH "))
                {
                    wingetPath = line.Substring("WINGET_PATH ".Length).Trim();
                }
                else if (line.StartsWith("COUNT "))
                {
                    _ = int.TryParse(line.Substring("COUNT ".Length).Trim(), out count);
                }
                else if (line.StartsWith("ITEM\t"))
                {
                    var parts = line.Split('\t');
                    if (parts.Length >= 4)
                    {
                        bool isPinned = parts.Length >= 5 && bool.TryParse(parts[4], out var p) && p;
                        items.Add(new JobItem(parts[1], parts[2], parts[3], isPinned));
                    }
                }
            }

            // 3. Setup cancellation listener
            using var cts = new CancellationTokenSource();
            Process? activeProcess = null;
            object processLock = new();

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        var msg = await reader.ReadLineAsync();
                        if (msg == null || msg == "CANCEL")
                        {
                            cts.Cancel();
                            lock (processLock)
                            {
                                try
                                {
                                    if (activeProcess != null && !activeProcess.HasExited)
                                    {
                                        activeProcess.Kill(true);
                                    }
                                }
                                catch { }
                            }
                            break;
                        }
                    }
                }
                catch { }
            });

            // 4. Execute packages
            int successCount = 0;
            int failedCount = 0;
            object writerLock = new();

            for (int i = 0; i < items.Count; i++)
            {
                if (cts.Token.IsCancellationRequested) break;

                var item = items[i];
                lock (writerLock)
                {
                    writer.WriteLine($"STEP\t{i + 1}\t{items.Count}\t{item.Name}\t{item.Id}\t{item.TargetVersion}");
                }

                var arg = $"upgrade --id \"{item.Id}\" --include-unknown --accept-source-agreements --accept-package-agreements";
                if (item.IsPinned)
                {
                    arg += " --include-pinned";
                }
                var psi = new ProcessStartInfo
                {
                    FileName = wingetPath,
                    Arguments = arg,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                int exitCode = -1;
                try
                {
                    using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                    lock (processLock)
                    {
                        activeProcess = proc;
                    }

                    proc.OutputDataReceived += (_, e) =>
                    {
                        if (e.Data != null)
                        {
                            lock (writerLock)
                            {
                                writer.WriteLine($"OUT\t{e.Data}");
                            }
                        }
                    };

                    proc.ErrorDataReceived += (_, e) =>
                    {
                        if (e.Data != null)
                        {
                            lock (writerLock)
                            {
                                writer.WriteLine($"OUT\t{e.Data}");
                            }
                        }
                    };

                    proc.Start();
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();

                    await proc.WaitForExitAsync(cts.Token);
                    exitCode = proc.ExitCode;
                }
                catch (OperationCanceledException)
                {
                    lock (writerLock)
                    {
                        writer.WriteLine("OUT\t\n[Notice] Operation was cancelled.\n");
                    }
                    break;
                }
                catch (Exception ex)
                {
                    lock (writerLock)
                    {
                        writer.WriteLine($"OUT\tError launching winget: {ex.Message}");
                    }
                }
                finally
                {
                    lock (processLock)
                    {
                        activeProcess = null;
                    }
                }

                if (exitCode == 0)
                {
                    successCount++;
                }
                else
                {
                    failedCount++;
                }

                lock (writerLock)
                {
                    writer.WriteLine($"STEP_DONE\t{exitCode}\t{item.Name}");
                }
            }

            lock (writerLock)
            {
                writer.WriteLine($"SUMMARY\t{successCount}\t{failedCount}");
                writer.WriteLine("COMPLETE");
            }
        }
        catch
        {
            // Socket closed or process terminated
        }
    }
}
