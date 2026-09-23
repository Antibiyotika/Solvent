using System.IO;
using System.Net.NetworkInformation;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using SolventUI.Models;

namespace SolventUI.Services;

// ---------------------------------------------------------------------
// Every actual "fix / check / clean" action Solvent performs, plus the
// Health Check aggregator that turns several of them into a 0-100 score.
// Kept in one file because they're all thin, closely related wrappers
// around ProcessRunner/PowerShell and were awkward to navigate spread
// across half a dozen near-identical small files.
// ---------------------------------------------------------------------

/// <summary>
/// C# port of tasks.cpp. Every long-running operation here is awaited
/// on a background thread by the caller (pages use `await Task.Run(...)`
/// the same way the original spun up worker threads), so the UI never
/// freezes. Methods that run more than one external step accept an
/// optional <see cref="IProgress{T}"/> so a page can show a real step
/// counter instead of just an indeterminate spinner, and every step
/// checks the <see cref="CancellationToken"/> so a Cancel button actually
/// stops the next external process from starting.
/// </summary>
public static class TaskService
{
    private static readonly LogService Log = LogService.Instance;

    // ---------- Repair & Security ----------

    public static async Task RepairSystemErrorsAsync(IProgress<TaskProgress>? progress = null, CancellationToken ct = default)
    {
        const int total = 3;
        try
        {
            await RestorePointService.EnsureBeforeAsync("system repair", progress, ct);

            progress?.Report(TaskProgress.Step(0, total, "Running SFC scan (sfc /scannow)..."));
            Log.Info("Repair: running SFC scan (sfc /scannow)...");
            var sfc = await ProcessRunner.RunAsync("sfc.exe", "/scannow", ct);
            Log.Info(sfc.Succeeded ? "SFC scan finished." : $"SFC returned exit code {sfc.ExitCode}.");

            progress?.Report(TaskProgress.Step(1, total, "Running DISM ScanHealth..."));
            Log.Info("Repair: DISM /ScanHealth...");
            var scan = await ProcessRunner.RunAsync("DISM.exe", "/Online /Cleanup-Image /ScanHealth", ct);
            Log.Info(scan.Succeeded ? "DISM ScanHealth finished." : $"DISM ScanHealth exit code {scan.ExitCode}.");

            progress?.Report(TaskProgress.Step(2, total, "Running DISM RestoreHealth..."));
            Log.Info("Repair: DISM /RestoreHealth...");
            var restore = await ProcessRunner.RunAsync("DISM.exe", "/Online /Cleanup-Image /RestoreHealth", ct);
            if (restore.Succeeded)
                Log.Success("System repair completed (SFC + DISM).");
            else
                Log.Warning($"DISM RestoreHealth exit code {restore.ExitCode}. Some errors may remain.");

            progress?.Report(TaskProgress.Step(total, total, "System repair finished."));
        }
        catch (OperationCanceledException)
        {
            Log.Warning("System repair cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"System repair failed: {ex.Message}");
        }
    }

    public static async Task EnableDefenderAsync(IProgress<TaskProgress>? progress = null, CancellationToken ct = default)
    {
        const int total = 3;
        try
        {
            progress?.Report(TaskProgress.Step(0, total, "Clearing Defender policy overrides..."));
            Log.Info("Defender: clearing policy overrides...");
            using (var policyKey = Registry.LocalMachine.CreateSubKey(
                       @"SOFTWARE\Policies\Microsoft\Windows Defender"))
            {
                policyKey?.DeleteValue("DisableAntiSpyware", throwOnMissingValue: false);
            }
            using (var rtpKey = Registry.LocalMachine.CreateSubKey(
                       @"SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection"))
            {
                rtpKey?.DeleteValue("DisableRealtimeMonitoring", throwOnMissingValue: false);
            }

            progress?.Report(TaskProgress.Step(1, total, "Starting the Windows Defender service..."));
            Log.Info("Defender: setting WinDefend service to automatic and starting it...");
            await ProcessRunner.RunAsync("sc.exe", "config WinDefend start= auto", ct);
            await ProcessRunner.RunAsync("sc.exe", "start WinDefend", ct);

            progress?.Report(TaskProgress.Step(2, total, "Enabling real-time protection..."));
            Log.Info("Defender: enabling real-time protection...");
            var ps = await ProcessRunner.RunPowerShellAsync(
                "Set-MpPreference -DisableRealtimeMonitoring $false", ct);

            if (ps.Succeeded)
                Log.Success("Windows Defender is enabled.");
            else
                Log.Warning("Defender configured, but PowerShell reported an issue enabling real-time protection.");

            progress?.Report(TaskProgress.Step(total, total, "Defender check finished."));
        }
        catch (OperationCanceledException)
        {
            Log.Warning("Enable Defender cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"Enable Defender failed: {ex.Message}");
        }
    }

    public static async Task CreateRestorePointAsync(IProgress<TaskProgress>? progress = null, CancellationToken ct = default)
    {
        try
        {
            progress?.Report(TaskProgress.Busy("Creating a system restore point..."));
            Log.Info("Creating a system restore point...");
            var ps = await ProcessRunner.RunPowerShellAsync(
                "Checkpoint-Computer -Description 'Solvent Restore Point' -RestorePointType MODIFY_SETTINGS", ct);

            if (ps.Succeeded)
                Log.Success("Restore point created.");
            else
                Log.Warning("Could not create a restore point (System Restore may be disabled on this drive).");

            progress?.Report(TaskProgress.Step(1, 1, "Restore point step finished."));
        }
        catch (OperationCanceledException)
        {
            Log.Warning("Restore point creation cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"Restore point creation failed: {ex.Message}");
        }
    }

    // ---------- Performance ----------

    public static async Task OptimizeSystemAsync(IProgress<TaskProgress>? progress = null, CancellationToken ct = default)
    {
        const int total = 4;
        await RestorePointService.EnsureBeforeAsync("Optimize", progress, ct);

        progress?.Report(TaskProgress.Step(0, total, "Switching to High Performance power plan..."));
        Log.Info("Optimize: switching to High Performance power plan...");
        await ProcessRunner.RunAsync("powercfg.exe", "/setactive SCHEME_MIN", ct);

        progress?.Report(TaskProgress.Step(1, total, "Flushing DNS cache..."));
        Log.Info("Optimize: flushing DNS cache...");
        await ProcessRunner.RunAsync("ipconfig.exe", "/flushdns", ct);

        progress?.Report(TaskProgress.Step(2, total, "Resetting Winsock..."));
        Log.Info("Optimize: resetting Winsock...");
        await ProcessRunner.RunAsync("netsh.exe", "winsock reset", ct);

        progress?.Report(TaskProgress.Step(3, total, "Optimizing system drive..."));
        var systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        // SSD-aware: a TRIM-only retrim on an SSD, a real defrag only on a
        // spinning disk — see PerformanceBoostService for why this replaced
        // an unconditional `defrag /O`.
        var drive = await PerformanceBoostService.OptimizeDriveAsync(systemDrive, progress, ct);

        if (drive.Succeeded)
            Log.Success($"System optimization completed ({(drive.Kind == DiskMediaKind.Ssd ? "SSD retrim" : "defrag")}).");
        else
            Log.Warning("Optimization finished with warnings on the drive step.");

        progress?.Report(TaskProgress.Step(total, total, "Optimization finished."));
    }

    public static async Task<string> CheckDiskHealthAsync(CancellationToken ct = default)
    {
        Log.Info("Reading disk health (S.M.A.R.T.)...");
        var ps = await ProcessRunner.RunPowerShellAsync(
            "Get-PhysicalDisk | Select-Object FriendlyName,HealthStatus,MediaType | Format-Table -AutoSize | Out-String -Width 200",
            ct);

        if (ps.Succeeded && !string.IsNullOrWhiteSpace(ps.StdOut))
        {
            Log.Success("Disk health summary retrieved.");
            return ps.StdOut.Trim();
        }

        Log.Warning("Could not read S.M.A.R.T. data (Get-PhysicalDisk may need elevation or isn't supported on this disk).");
        return "Disk health data unavailable.";
    }

    public static async Task<bool> TestInternetConnectionAsync(CancellationToken ct = default)
    {
        Log.Info("Testing internet connection (ping 1.1.1.1 / 8.8.8.8 + DNS)...");
        var ok = false;
        try
        {
            using var ping = new Ping();
            var r1 = await ping.SendPingAsync("1.1.1.1", 2000);
            ct.ThrowIfCancellationRequested();
            var r2 = await ping.SendPingAsync("8.8.8.8", 2000);
            ok = r1.Status == IPStatus.Success || r2.Status == IPStatus.Success;

            if (ok)
            {
                var dns = System.Net.Dns.GetHostEntry("www.microsoft.com");
                Log.Success($"Internet OK. DNS resolves ({dns.AddressList.Length} address(es)).");
            }
            else
            {
                Log.Error("No reply from 1.1.1.1 or 8.8.8.8. Connection may be down.");
            }
        }
        catch (OperationCanceledException)
        {
            Log.Warning("Internet test cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"Internet test failed: {ex.Message}");
        }
        return ok;
    }

    /// <summary>
    /// A deeper network fix than the DNS-flush/Winsock-reset step inside
    /// OptimizeSystemAsync: also resets the TCP/IP stack itself and
    /// releases/renews the IP lease. This is the standard "nothing else
    /// worked" sequence for connectivity issues (wrong IP, broken proxy,
    /// corrupted TCP/IP stack) and needs a restart to fully take effect.
    /// </summary>
    public static async Task RepairNetworkAsync(IProgress<TaskProgress>? progress = null, CancellationToken ct = default)
    {
        const int total = 5;
        try
        {
            await RestorePointService.EnsureBeforeAsync("network repair", progress, ct);

            progress?.Report(TaskProgress.Step(0, total, "Releasing current IP address..."));
            Log.Info("Network repair: releasing current IP address...");
            await ProcessRunner.RunAsync("ipconfig.exe", "/release", ct);

            progress?.Report(TaskProgress.Step(1, total, "Resetting TCP/IP stack..."));
            Log.Info("Network repair: resetting TCP/IP stack...");
            var tcpReset = await ProcessRunner.RunAsync("netsh.exe", "int ip reset", ct);

            progress?.Report(TaskProgress.Step(2, total, "Resetting Winsock catalog..."));
            Log.Info("Network repair: resetting Winsock catalog...");
            await ProcessRunner.RunAsync("netsh.exe", "winsock reset", ct);

            progress?.Report(TaskProgress.Step(3, total, "Flushing DNS cache..."));
            Log.Info("Network repair: flushing DNS cache...");
            await ProcessRunner.RunAsync("ipconfig.exe", "/flushdns", ct);

            progress?.Report(TaskProgress.Step(4, total, "Renewing IP address..."));
            Log.Info("Network repair: renewing IP address...");
            await ProcessRunner.RunAsync("ipconfig.exe", "/renew", ct);

            if (tcpReset.Succeeded)
                Log.Success("Network stack reset. Restart the PC for the TCP/IP reset to fully apply.");
            else
                Log.Warning("Network reset finished with warnings — a restart is recommended either way.");

            progress?.Report(TaskProgress.Step(total, total, "Network repair finished."));
        }
        catch (OperationCanceledException)
        {
            Log.Warning("Network repair cancelled — your connection may be left mid-reset; a restart is recommended.");
            throw;
        }
    }

    // ---------- Cleanup ----------

    /// <summary>
    /// Quick "safe defaults" clean, used by Health Check's one-click Fix and by
    /// Run All. Delegates to <see cref="CleanupScanService"/> so it shares the
    /// same, wider category list (and the Firefox-profile fix) as the Cleanup
    /// page's own scan-then-select flow, instead of keeping a second, narrower
    /// hardcoded sweep in sync by hand. Each run is added to the cleanup
    /// history (as a "quick" clean) so the Dashboard's totals include it.
    /// </summary>
    public static async Task<CleanupRunResult> CleanTempAndCacheAsync(IProgress<TaskProgress>? progress = null, CancellationToken ct = default)
    {
        var result = await CleanupScanService.CleanSelectedAsync(CleanupScanService.SafeDefaultIds, progress, ct);
        App.Services.GetRequiredService<CleanupHistoryService>().Record(result, CleanupHistoryService.SourceQuick);
        return result;
    }

    /// <summary>
    /// Every local fixed drive (skips removable/network/CD-ROM so a slow or
    /// absent USB stick can't hang the picker), for the Large Files drive
    /// selector. The user's profile folder comes first and is the default —
    /// fast, and covers the common case — with "all drives" and each
    /// individual drive offered as slower, opt-in alternatives.
    /// </summary>
    public static List<DriveOption> GetLargeFilesDriveOptions()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var options = new List<DriveOption>
        {
            new() { RootPath = profile, DisplayText = "Your user profile (fast)" },
            new() { RootPath = null, DisplayText = "This PC (all drives)" },
        };
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "" : $" ({d.VolumeLabel})";
                options.Add(new DriveOption { RootPath = d.RootDirectory.FullName, DisplayText = $"{d.Name}{label}" });
            }
        }
        catch { /* leave just the profile + "All drives" entries */ }
        return options;
    }

    /// <summary>
    /// Walks the given root(s) — a single drive, or every fixed drive when
    /// <paramref name="root"/> is null — and returns the largest files found:
    /// the "what's actually eating my disk" view Cleanup's temp-file sweep
    /// can't answer, since it only ever touches known cache/temp locations.
    /// </summary>
    public static Task<List<LargeFileInfo>> FindLargeFilesAsync(int topN = 200, long minSizeBytes = 50 * 1024 * 1024, string? root = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var roots = root is not null
                ? new List<string> { root }
                : DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                    .Select(d => d.RootDirectory.FullName).ToList();
            if (roots.Count == 0)
                roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

            Log.Info($"Scanning {string.Join(", ", roots)} for files over {SizeFormat.Format(minSizeBytes)}...");

            var results = new List<LargeFileInfo>();
            void Walk(string dir, int depth)
            {
                if (ct.IsCancellationRequested || depth > 14) return;
                string[] files, dirs;
                try
                {
                    files = Directory.GetFiles(dir);
                    dirs = Directory.GetDirectories(dir);
                }
                catch { return; } // access denied / junction loop — skip

                foreach (var f in files)
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        if (fi.Length < minSizeBytes) continue;
                        results.Add(new LargeFileInfo
                        {
                            FullPath = fi.FullName,
                            SizeBytes = fi.Length,
                            LastWriteTime = fi.LastWriteTime,
                        });
                    }
                    catch { /* file vanished / locked — skip */ }
                }

                foreach (var d in dirs)
                {
                    var name = Path.GetFileName(d);
                    // Skip noisy system/hidden/recovery/cache trees that aren't useful cleanup targets —
                    // AppData in particular is where Cleanup's own cache categories already look.
                    if (name is "AppData" or ".git" or "node_modules" or "$RECYCLE.BIN" or "System Volume Information" or "Windows" or "$WinREAgent")
                        continue;
                    Walk(d, depth + 1);
                }
            }

            foreach (var r in roots)
            {
                if (ct.IsCancellationRequested) break;
                Walk(r, 0);
            }

            var top = results.OrderByDescending(r => r.SizeBytes).Take(topN).ToList();
            Log.Success($"Large-file scan finished — {top.Count} file(s) over {SizeFormat.Format(minSizeBytes)} found.");
            return top;
        }, ct);

    /// <summary>
    /// Scans the common user-data folders (Desktop, Documents, Downloads,
    /// Pictures, Music, Videos — not the whole profile, to keep this fast)
    /// for files with identical content: group by size first (cheap), then
    /// confirm real duplicates within each size group with a SHA-256 hash.
    /// Within each group the newest file is left unselected; the rest are
    /// pre-checked as the likely copies to remove.
    /// </summary>
    public static Task<List<DuplicateFileGroup>> FindDuplicateFilesAsync(
        IProgress<TaskProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Path.Combine(userProfile, "Downloads"),
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            }.Distinct().Where(Directory.Exists).ToList();

            Log.Info("Duplicate scan: collecting files from Desktop, Documents, Downloads, Pictures, Music, and Videos...");
            progress?.Report(TaskProgress.Busy("Collecting files..."));

            var allFiles = new List<(string Path, long Size, DateTime Modified)>();
            void Walk(string dir, int depth)
            {
                if (ct.IsCancellationRequested || depth > 12) return;
                string[] files, dirs;
                try
                {
                    files = Directory.GetFiles(dir);
                    dirs = Directory.GetDirectories(dir);
                }
                catch { return; } // access denied / junction loop — skip

                foreach (var f in files)
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        if (fi.Length == 0) continue; // 0-byte files aren't meaningful duplicates
                        allFiles.Add((fi.FullName, fi.Length, fi.LastWriteTime));
                    }
                    catch { /* vanished / locked — skip */ }
                }

                foreach (var d in dirs)
                {
                    var name = Path.GetFileName(d);
                    if (name is ".git" or "node_modules" or "$RECYCLE.BIN") continue;
                    Walk(d, depth + 1);
                }
            }

            foreach (var root in roots) Walk(root, 0);
            ct.ThrowIfCancellationRequested();

            // Group by size first — a cheap way to rule out the vast majority
            // of files before paying for a hash. Only groups with more than
            // one file at the exact same size are worth hashing at all.
            var bySize = allFiles.GroupBy(f => f.Size).Where(g => g.Count() > 1).ToList();
            var candidateCount = bySize.Sum(g => g.Count());
            var processed = 0;
            var groups = new List<DuplicateFileGroup>();

            // Hashing is pure I/O + CPU per file with no shared state, so it
            // parallelizes cleanly — this used to hash every candidate one at a
            // time, which meant a profile with a few thousand same-size photos
            // or downloads could take minutes. Bounded by core count so it
            // doesn't thrash the disk on a spinning HDD.
            var degreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8);

            foreach (var sizeGroup in bySize)
            {
                ct.ThrowIfCancellationRequested();
                var byHash = new Dictionary<string, List<(string Path, DateTime Modified)>>();
                var hashResults = new (string Path, DateTime Modified, string? Hash)[sizeGroup.Count()];
                var sizeGroupFiles = sizeGroup.ToArray();

                Parallel.For(0, sizeGroupFiles.Length,
                    new ParallelOptions { MaxDegreeOfParallelism = degreeOfParallelism, CancellationToken = ct },
                    i =>
                    {
                        var (path, _, modified) = sizeGroupFiles[i];
                        var hash = TryHashFile(path);
                        hashResults[i] = (path, modified, hash);
                        var done = Interlocked.Increment(ref processed);
                        progress?.Report(TaskProgress.Step(done, candidateCount, $"Comparing files ({done}/{candidateCount})..."));
                    });

                foreach (var (path, modified, hash) in hashResults)
                {
                    if (hash is null) continue;
                    if (!byHash.TryGetValue(hash, out var list))
                        byHash[hash] = list = new List<(string, DateTime)>();
                    list.Add((path, modified));
                }

                foreach (var hashGroup in byHash.Values.Where(l => l.Count > 1))
                {
                    var files = hashGroup
                        .OrderByDescending(f => f.Modified)
                        .Select((f, i) => new DuplicateFileEntry
                        {
                            FullPath = f.Path,
                            LastWriteTime = f.Modified,
                            IsSelected = i > 0, // keep the newest copy unselected by default
                        })
                        .ToList();
                    groups.Add(new DuplicateFileGroup { SizeBytes = sizeGroup.Key, Files = files });
                }
            }

            groups = groups.OrderByDescending(g => g.SizeBytes * (g.Files.Count - 1)).ToList();
            var wasted = groups.Sum(g => g.SizeBytes * (g.Files.Count - 1));
            Log.Success(groups.Count == 0
                ? "No duplicate files found."
                : $"{groups.Count} duplicate group(s) found — {SizeFormat.Format(wasted)} could be freed.");

            return groups;
        }, ct);

    private static string? TryHashFile(string path)
    {
        try
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(stream));
        }
        catch
        {
            return null; // locked/inaccessible — treat as "can't confirm, skip"
        }
    }

    /// <summary>
    /// Deletes the given files to the Recycle Bin (not a permanent delete)
    /// so a wrong pick in the duplicate finder is still recoverable.
    /// </summary>
    public static Task<int> DeleteFilesToRecycleBinAsync(IEnumerable<string> paths, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var deleted = 0;
            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                        path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    deleted++;
                }
                catch (Exception ex)
                {
                    Log.Warning($"Could not delete {path}: {ex.Message}");
                }
            }
            Log.Success($"Sent {deleted} file(s) to the Recycle Bin.");
            return deleted;
        }, ct);

    // ---------- Run All ----------

    public static async Task RunAllAsync(IProgress<TaskProgress>? progress = null, CancellationToken ct = default)
    {
        const int total = 7;
        Log.Info("Run All: starting full maintenance pass...");
        SystemInfoService.GetSystemInfo();

        progress?.Report(TaskProgress.Step(0, total, "1/7 — Repairing system files..."));
        await RepairSystemErrorsAsync(null, ct);

        progress?.Report(TaskProgress.Step(1, total, "2/7 — Checking Windows Defender..."));
        await EnableDefenderAsync(null, ct);

        progress?.Report(TaskProgress.Step(2, total, "3/7 — Creating a restore point..."));
        await CreateRestorePointAsync(null, ct);

        progress?.Report(TaskProgress.Step(3, total, "4/7 — Optimizing system settings..."));
        await OptimizeSystemAsync(null, ct);

        progress?.Report(TaskProgress.Step(4, total, "5/7 — Checking disk health..."));
        await CheckDiskHealthAsync(ct);

        progress?.Report(TaskProgress.Step(5, total, "6/7 — Testing internet connection..."));
        await TestInternetConnectionAsync(ct);

        progress?.Report(TaskProgress.Step(6, total, "7/7 — Cleaning temp files and caches..."));
        await CleanTempAndCacheAsync(null, ct);

        progress?.Report(TaskProgress.Step(total, total, "Run All finished."));
        Log.Success("Run All: every task finished.");
    }

    public static async Task<string> ExportLogAsync(string filePath)
    {
        var lines = LogService.Instance.Entries.Select(e => $"[{e.TimeText}] [{e.Kind}] {e.Message}");
        await File.WriteAllLinesAsync(filePath, lines);
        return filePath;
    }
}

internal static class NativeMethods
{
    public const int SHERB_NOCONFIRMATION = 0x00000001;
    public const int SHERB_NOPROGRESSUI = 0x00000002;
    public const int SHERB_NOSOUND = 0x00000004;

    [System.Runtime.InteropServices.DllImport("Shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    public static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, int dwFlags);

    /// <summary>Layout required by SHQueryRecycleBin — cbSize must be set before calling.</summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    /// <summary>
    /// Reads the Recycle Bin's total size/item count across all drives (pszRootPath
    /// null) without emptying it — lets Cleanup show a real number before the user
    /// decides to empty it, instead of an unlabeled "Empty Recycle Bin" button.
    /// </summary>
    [System.Runtime.InteropServices.DllImport("Shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    public static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    /// <summary>Forces a process's working set pages back to the standby list without killing it — the safe, official basis for a "Free Up Memory" feature.</summary>
    [System.Runtime.InteropServices.DllImport("psapi.dll", SetLastError = true)]
    public static extern bool EmptyWorkingSet(IntPtr hProcess);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}

/// <summary>
/// Windows Update troubleshooting. The reset procedure below is the same
/// one Microsoft support scripts use: stop the update services, rename
/// the two caches so Windows rebuilds them from scratch, restart the
/// services. It fixes the large majority of "updates stuck / fail to
/// install / won't check" cases without touching anything else on disk.
/// </summary>
public static class WindowsUpdateService
{
    private static readonly LogService Log = LogService.Instance;

    public static async Task ResetComponentsAsync(IProgress<TaskProgress>? progress = null, CancellationToken ct = default)
    {
        const int total = 4;
        try
        {
            progress?.Report(TaskProgress.Step(0, total, "Stopping update-related services..."));
            Log.Info("Windows Update: stopping update-related services...");
            foreach (var svc in new[] { "wuauserv", "bits", "cryptsvc", "msiserver" })
                await ProcessRunner.RunAsync("net.exe", $"stop {svc}", ct);

            var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            var softwareDistribution = Path.Combine(systemRoot, "SoftwareDistribution");
            var catroot2 = Path.Combine(systemRoot, "System32", "catroot2");

            progress?.Report(TaskProgress.Step(1, total, "Clearing download and signature caches..."));
            Log.Info("Windows Update: clearing the download cache (SoftwareDistribution)...");
            RenameAside(softwareDistribution);

            Log.Info("Windows Update: clearing the signature cache (catroot2)...");
            RenameAside(catroot2);

            progress?.Report(TaskProgress.Step(2, total, "Resetting service security descriptors..."));
            Log.Info("Windows Update: resetting BITS and Windows Update to default security descriptors...");
            await ProcessRunner.RunAsync("sc.exe", "sdset bits D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;AU)(A;;CCLCSWRPWPDTLOCRRC;;;PU)", ct);
            await ProcessRunner.RunAsync("sc.exe", "sdset wuauserv D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;AU)(A;;CCLCSWRPWPDTLOCRRC;;;PU)", ct);

            progress?.Report(TaskProgress.Step(3, total, "Restarting services..."));
            Log.Info("Windows Update: restarting services...");
            foreach (var svc in new[] { "cryptsvc", "bits", "wuauserv", "msiserver" })
                await ProcessRunner.RunAsync("net.exe", $"start {svc}", ct);

            Log.Success("Windows Update components reset. Try checking for updates again.");
            progress?.Report(TaskProgress.Step(total, total, "Windows Update reset finished."));
        }
        catch (OperationCanceledException)
        {
            Log.Warning("Windows Update reset cancelled — some services may still be stopped; a restart is recommended.");
            throw;
        }
    }

    private static void RenameAside(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            var backup = path + ".bak-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            Directory.Move(path, backup);
        }
        catch (Exception ex)
        {
            Log.Warning($"Could not rename {path}: {ex.Message} (folder may still be in use — try again after a restart).");
        }
    }

    /// <summary>Opens the built-in Windows Update settings page and kicks off a check.</summary>
    public static Task OpenUpdateSettingsAsync(CancellationToken ct = default)
    {
        Log.Info("Opening Windows Update settings...");
        return ProcessRunner.RunAsync("cmd.exe", "/c start ms-settings:windowsupdate", ct);
    }

    /// <summary>True if a Windows Update install is waiting on a reboot to finish.</summary>
    public static bool IsRebootPending()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            return key != null;
        }
        catch
        {
            return false;
        }
    }
}

public sealed record DefenderStatus(bool RealTimeProtectionEnabled, bool AntivirusEnabled, string SignatureAgeText);

/// <summary>
/// Wraps the Windows Defender PowerShell module (built into every
/// Windows 10/11 install, no extra download) to run a real quick scan
/// and read real protection status — not just toggle the service on/off
/// like <see cref="TaskService.EnableDefenderAsync"/> does.
/// </summary>
public static class MalwareScanService
{
    private static readonly LogService Log = LogService.Instance;

    public static async Task<DefenderStatus?> GetStatusAsync(CancellationToken ct = default)
    {
        var ps = await ProcessRunner.RunPowerShellAsync(
            "Get-MpComputerStatus | Select-Object RealTimeProtectionEnabled,AntivirusEnabled,AntivirusSignatureAge " +
            "| ConvertTo-Json -Compress", ct);

        if (!ps.Succeeded || string.IsNullOrWhiteSpace(ps.StdOut))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(ps.StdOut);
            var root = doc.RootElement;
            var rtp = root.TryGetProperty("RealTimeProtectionEnabled", out var r) && r.GetBoolean();
            var av = root.TryGetProperty("AntivirusEnabled", out var a) && a.GetBoolean();
            var age = root.TryGetProperty("AntivirusSignatureAge", out var g) ? g.GetInt32() : -1;
            var ageText = age < 0 ? "unknown" : age == 0 ? "up to date" : $"{age} day(s) old";
            return new DefenderStatus(rtp, av, ageText);
        }
        catch
        {
            return null;
        }
    }

    public static async Task RunQuickScanAsync(IProgress<TaskProgress>? progress = null, CancellationToken ct = default)
    {
        const int total = 2;
        try
        {
            progress?.Report(TaskProgress.Step(0, total, "Starting Defender quick scan (this can take a few minutes)..."));
            Log.Info("Malware scan: starting Windows Defender quick scan (this can take a few minutes)...");
            var ps = await ProcessRunner.RunPowerShellAsync("Start-MpScan -ScanType QuickScan", ct);

            if (!ps.Succeeded)
            {
                Log.Warning("Defender quick scan could not be started (Defender may be managed by another antivirus or disabled by policy).");
                return;
            }

            progress?.Report(TaskProgress.Step(1, total, "Checking scan results..."));
            var threats = await ProcessRunner.RunPowerShellAsync(
                "(Get-MpThreatDetection | Measure-Object).Count", ct);

            if (threats.Succeeded && int.TryParse(threats.StdOut.Trim(), out var count) && count > 0)
                Log.Warning($"Quick scan finished — Defender has {count} threat detection(s) on record. Open Windows Security to review.");
            else
                Log.Success("Quick scan finished — no active threats found.");

            progress?.Report(TaskProgress.Step(total, total, "Scan finished."));
        }
        catch (OperationCanceledException)
        {
            Log.Warning("Malware scan cancelled.");
            throw;
        }
    }
}

/// <summary>
/// Thin wrapper around <c>mdsched.exe</c>, the Windows Memory Diagnostic
/// tool that's been built into Windows since Vista. It schedules a RAM
/// test on the next restart and reports results in Event Viewer — real
/// hardware-level diagnostics Solvent doesn't need to reimplement.
/// </summary>
public static class MemoryDiagnosticService
{
    private static readonly LogService Log = LogService.Instance;

    public static async Task ScheduleTestAsync(IProgress<TaskProgress>? progress = null, CancellationToken ct = default)
    {
        try
        {
            progress?.Report(TaskProgress.Busy("Launching Windows Memory Diagnostic..."));
            Log.Info("Memory test: launching Windows Memory Diagnostic — choose \"Restart now\" to run it immediately.");
            await ProcessRunner.RunAsync("mdsched.exe", "", ct);
            progress?.Report(TaskProgress.Step(1, 1, "Memory Diagnostic launched."));
        }
        catch (OperationCanceledException)
        {
            Log.Warning("Memory test cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not launch Windows Memory Diagnostic: {ex.Message}");
        }
    }
}

public sealed record DriverUpdateInfo(string Title, string? Manufacturer, string? VersionDate);

/// <summary>
/// Checks for pending driver updates the same way Settings &gt; Windows
/// Update does: through the local Windows Update Agent COM API
/// (Microsoft.Update.Session), filtered to Type='Driver'. Read-only — it
/// only reports what Windows Update already knows is available. Installing
/// is left to Windows Update itself via
/// <see cref="WindowsUpdateService.OpenUpdateSettingsAsync"/>, since driver
/// installs can require a restart and Solvent shouldn't push that from a
/// background PowerShell call.
/// </summary>
public static class DriverUpdateService
{
    private static readonly LogService Log = LogService.Instance;

    private const string SearchScript =
        "$s = New-Object -ComObject Microsoft.Update.Session; " +
        "$searcher = $s.CreateUpdateSearcher(); " +
        "$result = $searcher.Search(\"IsInstalled=0 and Type='Driver'\"); " +
        "$list = @(); " +
        "foreach ($u in $result.Updates) { " +
        "$verDate = if ($u.DriverVerDate) { ([datetime]$u.DriverVerDate).ToString('yyyy-MM-dd') } else { $null }; " +
        "$list += [PSCustomObject]@{ Title = $u.Title; Manufacturer = $u.DriverManufacturer; VersionDate = $verDate } " +
        "}; " +
        "ConvertTo-Json -InputObject $list -Compress";

    public static async Task<List<DriverUpdateInfo>> CheckForUpdatesAsync(
        IProgress<TaskProgress>? progress = null, CancellationToken ct = default)
    {
        var results = new List<DriverUpdateInfo>();
        try
        {
            progress?.Report(TaskProgress.Busy("Asking Windows Update for pending driver updates (this can take a minute)..."));
            Log.Info("Checking Windows Update for pending driver updates (this can take a minute)...");

            var ps = await ProcessRunner.RunPowerShellAsync(SearchScript, ct);

            if (!ps.Succeeded)
            {
                Log.Warning("Could not reach the Windows Update Agent to check for driver updates.");
                return results;
            }

            var json = ps.StdOut.Trim();
            if (string.IsNullOrWhiteSpace(json))
                json = "[]";

            using var doc = JsonDocument.Parse(json);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var title = item.TryGetProperty("Title", out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(title)) continue;

                var mfr = item.TryGetProperty("Manufacturer", out var m) ? m.GetString() : null;
                var ver = item.TryGetProperty("VersionDate", out var v) ? v.GetString() : null;
                results.Add(new DriverUpdateInfo(title!, mfr, ver));
            }

            progress?.Report(TaskProgress.Step(1, 1, "Driver update check finished."));
            Log.Success(results.Count == 0
                ? "No pending driver updates found."
                : $"{results.Count} driver update(s) available through Windows Update.");
        }
        catch (OperationCanceledException)
        {
            Log.Warning("Driver update check cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning($"Driver update check failed: {ex.Message}");
        }
        return results;
    }
}

public sealed record HealthCheckResult(int Score, List<DiagnosticIssue> Issues);

/// <summary>
/// The actual "problem solver" part of Solvent: runs every real check the
/// app has (crash history, driver errors, driver updates, Windows Update
/// state, Defender state, disk free space) in parallel, turns them into a
/// 0-100 health score, and hands back a flat list of issues each page can
/// render with a one-click fix button next to it.
/// </summary>
public static class DiagnosticsService
{
    private static readonly LogService Log = LogService.Instance;

    public static async Task<HealthCheckResult> RunHealthCheckAsync(CancellationToken ct = default)
    {
        Log.Info("Health check: scanning crash history, drivers, updates, security, disk health, and disk space...");

        var crashTask = EventLogService.GetRecentCrashSummaryAsync();
        var bugChecksTask = BugCheckService.GetRecentBugChecksAsync();
        var devicesTask = DeviceHealthService.GetProblemDevicesAsync();
        var defenderTask = MalwareScanService.GetStatusAsync(ct);
        var diskTask = TaskService.CheckDiskHealthAsync(ct);
        var driverUpdatesTask = DriverUpdateService.CheckForUpdatesAsync(null, ct);
        var firewallTask = SecurityHealthService.GetFirewallStatusAsync(ct);
        var diskHealthTask = SecurityHealthService.GetDiskHealthStatusesAsync(ct);
        var bitlockerTask = SecurityHealthService.IsSystemDriveEncryptedAsync(ct);
        var restorePointsTask = RestorePointService.ListAsync(ct);

        await Task.WhenAll(crashTask, bugChecksTask, devicesTask, defenderTask, diskTask, driverUpdatesTask,
            firewallTask, diskHealthTask, bitlockerTask, restorePointsTask);

        var issues = new List<DiagnosticIssue>();
        var score = 100;

        // --- Crash history ---
        var crash = crashTask.Result;
        if (crash.UnexpectedShutdowns > 0)
        {
            score -= Math.Min(20, crash.UnexpectedShutdowns * 5);
            issues.Add(new DiagnosticIssue
            {
                Title = $"{crash.UnexpectedShutdowns} unexpected shutdown(s) in the last 7 days",
                Detail = "Windows recorded a hard shutdown or power loss (Kernel-Power 41). Often a driver, PSU, or overheating issue.",
                Severity = IssueSeverity.Warning,
            });
        }
        if (crash.CriticalSystemErrors > 0)
        {
            score -= Math.Min(20, crash.CriticalSystemErrors * 4);

            // Prefer specific, decoded BSOD entries over the generic "the log
            // has critical entries" message whenever we actually decoded any —
            // same score impact either way, just a more useful Detail.
            var bugChecks = bugChecksTask.Result
                .GroupBy(b => b.CodeHex)
                .Select(g => g.OrderByDescending(b => b.When).First())
                .OrderByDescending(b => b.When)
                .Take(3)
                .ToList();

            if (bugChecks.Count > 0)
            {
                var loc = LocalizationService.Instance;
                foreach (var bc in bugChecks)
                {
                    var explanation = loc.Get($"Diag_BugCheckCat_{bc.ExplanationKey}");
                    issues.Add(new DiagnosticIssue
                    {
                        Title = string.Format(loc.Get("Diag_BugCheckTitleFormat"), bc.Name, bc.CodeHex),
                        Detail = string.Format(loc.Get("Diag_BugCheckDetailFormat"), bc.When.ToLocalTime(), explanation),
                        Severity = IssueSeverity.Critical,
                    });
                }
            }
            else
            {
                issues.Add(new DiagnosticIssue
                {
                    Title = $"{crash.CriticalSystemErrors} critical system error(s) logged recently",
                    Detail = "The System event log has critical-level entries — a driver or service is failing.",
                    Severity = IssueSeverity.Warning,
                });
            }
        }
        if (crash.AppCrashes > 5)
        {
            issues.Add(new DiagnosticIssue
            {
                Title = $"{crash.AppCrashes} application crash(es) recently",
                Detail = "Several apps have crashed in the last 7 days. Usually app-specific, but worth watching.",
                Severity = IssueSeverity.Info,
            });
        }

        // --- Driver / device problems ---
        var devices = devicesTask.Result;
        if (devices.Count > 0)
        {
            score -= Math.Min(25, devices.Count * 8);
            var names = string.Join(", ", devices.Take(3).Select(d => d.Name));
            issues.Add(new DiagnosticIssue
            {
                Title = $"{devices.Count} device(s) have driver problems",
                Detail = $"Device Manager would flag these: {names}{(devices.Count > 3 ? ", ..." : "")}.",
                Severity = IssueSeverity.Critical,
                FixActionId = "rescan-devices",
                FixLabel = "Rescan hardware",
            });
        }

        // --- Driver updates available ---
        var driverUpdates = driverUpdatesTask.Result;
        if (driverUpdates.Count > 0)
        {
            issues.Add(new DiagnosticIssue
            {
                Title = $"{driverUpdates.Count} driver update(s) available",
                Detail = "Windows Update has newer drivers ready to install for this PC.",
                Severity = IssueSeverity.Info,
                FixActionId = "open-driver-updates",
                FixLabel = "Open Windows Update",
            });
        }

        // --- Windows Update ---
        if (WindowsUpdateService.IsRebootPending())
        {
            score -= 5;
            issues.Add(new DiagnosticIssue
            {
                Title = "A Windows Update is waiting on a restart",
                Detail = "An update has finished installing but needs a reboot to take effect.",
                Severity = IssueSeverity.Info,
            });
        }

        // --- Defender ---
        var defender = defenderTask.Result;
        if (defender is null)
        {
            issues.Add(new DiagnosticIssue
            {
                Title = "Couldn't read Windows Defender status",
                Detail = "Defender may be replaced by another antivirus, or its PowerShell module is unavailable.",
                Severity = IssueSeverity.Info,
            });
        }
        else if (!defender.RealTimeProtectionEnabled)
        {
            score -= 20;
            issues.Add(new DiagnosticIssue
            {
                Title = "Real-time protection is off",
                Detail = "Windows Defender is installed but not actively protecting this PC right now.",
                Severity = IssueSeverity.Critical,
                FixActionId = "enable-defender",
                FixLabel = "Enable Defender",
            });
        }

        // --- Disk free space ---
        try
        {
            var systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
            var drive = new DriveInfo(systemDrive);
            if (drive.IsReady)
            {
                var freePct = drive.TotalFreeSpace * 100.0 / drive.TotalSize;
                if (freePct < 10)
                {
                    score -= 15;
                    issues.Add(new DiagnosticIssue
                    {
                        Title = $"System drive is only {freePct:0.#}% free",
                        Detail = $"{drive.Name} has {SizeFormat.Format(drive.TotalFreeSpace)} free of {SizeFormat.Format(drive.TotalSize)}. Low disk space slows Windows down and can block updates.",
                        Severity = freePct < 5 ? IssueSeverity.Critical : IssueSeverity.Warning,
                        FixActionId = "clean-temp",
                        FixLabel = "Clean up now",
                    });
                }
            }
        }
        catch { /* ignore */ }

        // --- RAM pressure ---
        try
        {
            var snapshot = ResourceMonitorService.GetSnapshot();
            if (snapshot.RamTotalGb > 0 && snapshot.RamPercent >= 90)
            {
                score -= 10;
                issues.Add(new DiagnosticIssue
                {
                    Title = $"Memory usage is at {snapshot.RamPercent:0}%",
                    Detail = $"{snapshot.RamText} in use. Trimming background processes can help, or check Task Manager for what's using the most memory.",
                    Severity = IssueSeverity.Warning,
                    FixActionId = "free-memory",
                    FixLabel = "Free up memory",
                });
            }
        }
        catch { /* ignore */ }

        // --- Print spooler ---
        var queuedJobs = PrinterRepairService.GetQueuedJobCount();
        if (queuedJobs > 3)
        {
            issues.Add(new DiagnosticIssue
            {
                Title = $"{queuedJobs} print job file(s) stuck in the queue",
                Detail = "The Print Spooler may be jammed — this is the usual cause of \"nothing will print\".",
                Severity = IssueSeverity.Warning,
                FixActionId = "repair-printer",
                FixLabel = "Repair printer",
            });
        }

        // --- Firewall ---
        var firewall = firewallTask.Result;
        if (firewall is not null && !firewall.AllEnabled)
        {
            score -= 15;
            issues.Add(new DiagnosticIssue
            {
                Title = $"Windows Firewall is off for: {string.Join(", ", firewall.DisabledProfiles)}",
                Detail = "With the firewall off on an active network profile, this PC is more exposed to other devices on the same network.",
                Severity = IssueSeverity.Critical,
                FixActionId = "enable-firewall",
                FixLabel = "Enable firewall",
            });
        }

        // --- Disk S.M.A.R.T. health ---
        var diskHealths = diskHealthTask.Result;
        var unhealthyDisks = diskHealths.Where(d => !d.HealthStatus.Equals("Healthy", StringComparison.OrdinalIgnoreCase)).ToList();
        if (unhealthyDisks.Count > 0)
        {
            score -= 25;
            var names = string.Join(", ", unhealthyDisks.Select(d => $"{d.Name} ({d.HealthStatus})"));
            issues.Add(new DiagnosticIssue
            {
                Title = $"{unhealthyDisks.Count} disk(s) reporting degraded health",
                Detail = $"{names}. Back up anything important soon — a disk flagged this way can fail without further warning.",
                Severity = IssueSeverity.Critical,
            });
        }

        // --- BitLocker ---
        var encrypted = bitlockerTask.Result;
        if (encrypted == false)
        {
            issues.Add(new DiagnosticIssue
            {
                Title = "System drive isn't encrypted",
                Detail = "Turning on BitLocker protects the files on this PC if it's ever lost or stolen.",
                Severity = IssueSeverity.Info,
                FixActionId = "open-bitlocker",
                FixLabel = "Open BitLocker settings",
            });
        }

        // --- Restore points ---
        var restorePoints = restorePointsTask.Result;
        var newestRestorePoint = restorePoints.Count > 0 ? restorePoints[0] : null; // ListAsync already orders newest-first
        if (newestRestorePoint is null || newestRestorePoint.CreationTime < DateTime.Now.AddDays(-30))
        {
            issues.Add(new DiagnosticIssue
            {
                Title = newestRestorePoint is null
                    ? "No system restore point exists"
                    : $"Newest restore point is {(DateTime.Now - newestRestorePoint.CreationTime).Days} days old",
                Detail = "A recent restore point makes it easy to undo a bad driver or update. Creating one takes a few seconds.",
                Severity = IssueSeverity.Info,
                FixActionId = "create-restore-point",
                FixLabel = "Create restore point",
            });
        }

        // --- Startup load ---
        var enabledStartupCount = StartupManagerService.GetStartupItems().Count(i => i.IsEnabled);
        if (enabledStartupCount > 12)
        {
            issues.Add(new DiagnosticIssue
            {
                Title = $"{enabledStartupCount} apps launch at startup",
                Detail = "A long startup list is one of the most common causes of a slow boot. Worth reviewing what actually needs to launch automatically.",
                Severity = IssueSeverity.Info,
                FixActionId = "open-startup-manager",
                FixLabel = "Review startup apps",
            });
        }

        if (issues.Count == 0)
        {
            issues.Add(new DiagnosticIssue
            {
                Title = "No problems found",
                Detail = "Crash history, drivers, Windows Update, Defender, firewall, disk health, and disk space all look healthy.",
                Severity = IssueSeverity.Good,
            });
        }

        score = Math.Clamp(score, 0, 100);
        var result = new HealthCheckResult(score, issues.OrderByDescending(i => i.Severity).ToList());
        Log.Success($"Health check finished — score {score}/100, {issues.Count(i => i.Severity != IssueSeverity.Good)} issue(s) found.");

        // Cache the result and notify whoever's listening (the Health Check
        // page) so a check triggered from "Run All" — or from any other
        // page — still shows up there instead of only in the activity log.
        LastResult = result;
        LastResultChanged?.Invoke(result);

        return result;
    }

    /// <summary>Most recent result, if any check has run since the app started — lets the Health Check page show it immediately on navigating in, even if the check was actually triggered by "Run All" on another page.</summary>
    public static HealthCheckResult? LastResult { get; private set; }

    /// <summary>Raised every time a new health check result is produced, from any page.</summary>
    public static event Action<HealthCheckResult>? LastResultChanged;

    /// <summary>
    /// Writes a Health Check result to disk as either a printable HTML
    /// report (open it and use the browser's Print &gt; Save as PDF for a
    /// PDF copy — no extra PDF library needed) or a plain-text summary,
    /// chosen by the file's extension.
    /// </summary>
    public static async Task ExportReportAsync(HealthCheckResult result, string filePath)
    {
        var isHtml = filePath.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                     || filePath.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);

        var content = isHtml ? BuildReportHtml(result) : BuildReportText(result);
        await File.WriteAllTextAsync(filePath, content);
        Log.Success($"Health report saved to {filePath}.");
    }

    private static string BuildReportText(HealthCheckResult result)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("SOLVENT — HEALTH CHECK REPORT");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"Health score: {result.Score}/100");
        sb.AppendLine(new string('-', 40));

        foreach (var issue in result.Issues)
        {
            sb.AppendLine($"[{issue.Severity}] {issue.Title}");
            sb.AppendLine($"    {issue.Detail}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string BuildReportHtml(HealthCheckResult result)
    {
        static string Esc(string s) => System.Net.WebUtility.HtmlEncode(s);
        static string SeverityColor(IssueSeverity s) => s switch
        {
            IssueSeverity.Critical => "#ef4444",
            IssueSeverity.Warning => "#f59e0b",
            IssueSeverity.Good => "#22c55e",
            _ => "#60a5fa",
        };

        var rows = string.Join("\n", result.Issues.Select(i => $@"
            <tr>
                <td style=""border-left:4px solid {SeverityColor(i.Severity)};padding:10px 14px;"">
                    <div style=""font-weight:600;color:#e5e7eb;"">{Esc(i.Title)}</div>
                    <div style=""color:#9ca3af;font-size:13px;margin-top:4px;"">{Esc(i.Detail)}</div>
                </td>
            </tr>"));

        return $@"<!DOCTYPE html>
<html>
<head>
<meta charset=""utf-8"" />
<title>Solvent Health Check Report</title>
<style>
  body {{ background:#0f0f12; color:#e5e7eb; font-family: Segoe UI, Arial, sans-serif; margin:40px; }}
  h1 {{ font-size:20px; }}
  .score {{ font-size:48px; font-weight:bold; color:#22c55e; }}
  .meta {{ color:#9ca3af; font-size:13px; margin-bottom:24px; }}
  table {{ width:100%; border-collapse:collapse; background:#17171b; border-radius:8px; overflow:hidden; }}
</style>
</head>
<body>
  <h1>Solvent — Health Check Report</h1>
  <div class=""meta"">Generated {DateTime.Now:yyyy-MM-dd HH:mm}</div>
  <div class=""score"">{result.Score}/100</div>
  <table>{rows}</table>
</body>
</html>";
    }

}

/// <summary>
/// Fixes the single most common "nothing prints" cause: a stuck Print
/// Spooler with a jammed job in its queue. Stops the service (a job can't
/// be cleared while the service holds the file open), deletes everything
/// under spool\PRINTERS, then starts the service again — the same manual
/// sequence in every printer-troubleshooting guide, just one click.
/// </summary>
public static class PrinterRepairService
{
    private static readonly LogService Log = LogService.Instance;

    public static async Task RepairSpoolerAsync(IProgress<TaskProgress>? progress = null, CancellationToken ct = default)
    {
        const int total = 3;
        try
        {
            progress?.Report(TaskProgress.Step(0, total, "Stopping the Print Spooler service..."));
            Log.Info("Printer repair: stopping the Print Spooler service...");
            await ProcessRunner.RunAsync("net.exe", "stop spooler", ct);

            progress?.Report(TaskProgress.Step(1, total, "Clearing stuck print jobs..."));
            var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            var spoolFolder = Path.Combine(systemRoot, "System32", "spool", "PRINTERS");
            Log.Info($"Printer repair: clearing stuck jobs from {spoolFolder}...");
            var cleared = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(spoolFolder))
                {
                    try { File.Delete(file); cleared++; } catch { /* in use — skip, spooler is stopped so this should be rare */ }
                }
            }
            catch { /* folder missing/inaccessible — nothing to clear */ }

            progress?.Report(TaskProgress.Step(2, total, "Restarting the Print Spooler service..."));
            Log.Info("Printer repair: restarting the Print Spooler service...");
            var start = await ProcessRunner.RunAsync("net.exe", "start spooler", ct);

            if (start.Succeeded)
                Log.Success($"Print Spooler restarted and {cleared} stuck job file(s) cleared. Try printing again.");
            else
                Log.Warning("Print Spooler did not report a clean restart — try again or check Services (services.msc).");

            progress?.Report(TaskProgress.Step(total, total, "Printer repair finished."));
        }
        catch (OperationCanceledException)
        {
            Log.Warning("Printer repair cancelled — the Spooler service may still be stopped; a restart is recommended.");
            throw;
        }
    }

    /// <summary>Number of files currently sitting in the spool folder — a simple "is a job stuck" signal for the health check.</summary>
    public static int GetQueuedJobCount()
    {
        try
        {
            var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            var spoolFolder = Path.Combine(systemRoot, "System32", "spool", "PRINTERS");
            return Directory.Exists(spoolFolder) ? Directory.GetFiles(spoolFolder, "*.spl").Length : 0;
        }
        catch
        {
            return 0;
        }
    }
}

/// <summary>
/// Switches the active network adapter's DNS servers between Windows'
/// default (automatic, via DHCP) and a fast public resolver (Cloudflare
/// 1.1.1.1 / 1.0.0.1). A slow or flaky ISP DNS server is a very common,
/// easy-to-fix cause of "the internet is slow" or "some sites won't load"
/// even when the connection itself is fine.
/// </summary>
public static class NetworkDnsService
{
    private static readonly LogService Log = LogService.Instance;

    public static async Task SetPublicDnsAsync(IProgress<TaskProgress>? progress = null, CancellationToken ct = default)
    {
        var adapter = await GetActiveAdapterNameAsync(ct);
        if (adapter is null)
        {
            Log.Warning("Could not find an active network adapter to configure.");
            return;
        }

        progress?.Report(TaskProgress.Busy($"Setting DNS on \"{adapter}\" to Cloudflare (1.1.1.1 / 1.0.0.1)..."));
        Log.Info($"Setting DNS on \"{adapter}\" to Cloudflare (1.1.1.1 / 1.0.0.1)...");
        await ProcessRunner.RunAsync("netsh.exe", $"interface ip set dns name=\"{adapter}\" static 1.1.1.1 primary", ct);
        await ProcessRunner.RunAsync("netsh.exe", $"interface ip add dns name=\"{adapter}\" 1.0.0.1 index=2", ct);
        await ProcessRunner.RunAsync("ipconfig.exe", "/flushdns", ct);

        Log.Success($"DNS on \"{adapter}\" set to Cloudflare. Use \"Automatic (DHCP)\" to undo this.");
        progress?.Report(TaskProgress.Step(1, 1, "DNS updated."));
    }

    public static async Task ResetDnsToAutomaticAsync(IProgress<TaskProgress>? progress = null, CancellationToken ct = default)
    {
        var adapter = await GetActiveAdapterNameAsync(ct);
        if (adapter is null)
        {
            Log.Warning("Could not find an active network adapter to configure.");
            return;
        }

        progress?.Report(TaskProgress.Busy($"Resetting DNS on \"{adapter}\" to automatic (DHCP)..."));
        Log.Info($"Resetting DNS on \"{adapter}\" to automatic (DHCP)...");
        await ProcessRunner.RunAsync("netsh.exe", $"interface ip set dns name=\"{adapter}\" dhcp", ct);
        await ProcessRunner.RunAsync("ipconfig.exe", "/flushdns", ct);

        Log.Success($"DNS on \"{adapter}\" reset to automatic.");
        progress?.Report(TaskProgress.Step(1, 1, "DNS reset."));
    }

    private static async Task<string?> GetActiveAdapterNameAsync(CancellationToken ct)
    {
        var ps = await ProcessRunner.RunPowerShellAsync(
            "(Get-NetAdapter | Where-Object Status -eq 'Up' | Sort-Object -Property InterfaceMetric | Select-Object -First 1 -ExpandProperty Name)",
            ct);
        var name = ps.StdOut.Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}

/// <summary>
/// Schedules a full CHKDSK (file-system + bad-sector scan) on the system
/// drive for the next restart — the standard fix for file-system
/// corruption, random crashes traced to disk errors, or "this drive needs
/// to be scanned" notifications. CHKDSK can't lock the boot volume while
/// Windows is running, so it always answers Windows' own "run at next
/// restart?" prompt with Y rather than actually running immediately.
/// </summary>
public static class DiskCheckService
{
    private static readonly LogService Log = LogService.Instance;

    public static async Task ScheduleCheckAsync(IProgress<TaskProgress>? progress = null, CancellationToken ct = default)
    {
        var systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        try
        {
            progress?.Report(TaskProgress.Busy($"Scheduling a disk check on {systemDrive} for the next restart..."));
            Log.Info($"Scheduling CHKDSK on {systemDrive} (file system + bad sectors) for the next restart...");

            // chkdsk asks "Would you like to schedule this volume to be checked
            // the next time the system restarts? (Y/N)" when the volume is in
            // use (always true for the boot drive) — answer it via stdin.
            var result = await ProcessRunner.RunAsync("chkdsk.exe", $"{systemDrive} /f /r", ct, stdInput: "Y");

            if (result.StdOut.Contains("scheduled", StringComparison.OrdinalIgnoreCase) || result.Succeeded)
                Log.Success($"Disk check scheduled — {systemDrive} will be scanned the next time this PC restarts.");
            else
                Log.Warning("Could not confirm the disk check was scheduled — restart and watch for the CHKDSK screen to be sure.");

            progress?.Report(TaskProgress.Step(1, 1, "Disk check scheduled."));
        }
        catch (OperationCanceledException)
        {
            Log.Warning("Disk check scheduling cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not schedule the disk check: {ex.Message}");
        }
    }
}

/// <summary>
/// Wraps <c>powercfg /batteryreport</c> — a built-in Windows tool (laptops
/// only) that logs battery wear over time: design capacity vs current full
/// charge, recent charge cycles, and usage history. Solvent just generates
/// it as HTML into the user's Documents folder and opens it, the same as
/// running the command from an elevated prompt yourself.
/// </summary>
public static class BatteryReportService
{
    private static readonly LogService Log = LogService.Instance;

    public static async Task GenerateAndOpenAsync(IProgress<TaskProgress>? progress = null, CancellationToken ct = default)
    {
        try
        {
            progress?.Report(TaskProgress.Busy("Generating battery report..."));
            Log.Info("Generating battery health report (powercfg /batteryreport)...");

            var folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var path = Path.Combine(folder, $"Solvent-BatteryReport-{DateTime.Now:yyyy-MM-dd}.html");

            var result = await ProcessRunner.RunAsync("powercfg.exe", $"/batteryreport /output \"{path}\"", ct);

            if (!result.Succeeded || !File.Exists(path))
            {
                Log.Warning("Could not generate a battery report — this PC may not have a battery (desktop).");
                return;
            }

            Log.Success($"Battery report saved to {path}. Opening it now...");
            ProcessRunner.LaunchDetached(path);
            progress?.Report(TaskProgress.Step(1, 1, "Battery report ready."));
        }
        catch (OperationCanceledException)
        {
            Log.Warning("Battery report cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not generate the battery report: {ex.Message}");
        }
    }
}

/// <summary>
/// Read-only view of System Restore checkpoints via the
/// <c>Get-ComputerRestorePoint</c> PowerShell cmdlet, plus a launcher for
/// the built-in restore wizard (<c>rstrui.exe</c>). Actually rolling the
/// system back is left to that wizard rather than scripted with
/// <c>Restore-Computer</c>: a restore forces an immediate reboot into a
/// recovery environment, and Windows' own picker already shows exactly
/// which point is selected and what it will affect before committing —
/// not a step worth reimplementing unattended.
/// </summary>
public static class RestorePointService
{
    private static readonly LogService Log = LogService.Instance;

    public static async Task<List<RestorePointInfo>> ListAsync(CancellationToken ct = default)
    {
        var results = new List<RestorePointInfo>();
        try
        {
            var ps = await ProcessRunner.RunPowerShellAsync(
                "Get-ComputerRestorePoint | Select-Object SequenceNumber,Description,CreationTime | ConvertTo-Json -Compress",
                ct);

            if (!ps.Succeeded || string.IsNullOrWhiteSpace(ps.StdOut))
                return results;

            var json = ps.StdOut.Trim();
            using var doc = JsonDocument.Parse(json.StartsWith('[') ? json : $"[{json}]");
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var seq = item.TryGetProperty("SequenceNumber", out var s) ? s.GetInt32() : 0;
                var desc = item.TryGetProperty("Description", out var d) ? d.GetString() ?? "" : "";
                DateTime created = default;
                if (item.TryGetProperty("CreationTime", out var c) && c.ValueKind == JsonValueKind.String)
                    created = ParseCreationTime(c.GetString());
                results.Add(new RestorePointInfo(seq, desc, created));
            }
        }
        catch
        {
            // System Restore may be disabled on this drive, or the module unavailable — return what we have.
        }
        return results.OrderByDescending(r => r.CreationTime).ToList();
    }

    /// <summary>
    /// Get-ComputerRestorePoint's CreationTime comes through ConvertTo-Json
    /// in one of two shapes: a JSON-style "/Date(1700000000000)/" epoch, or a
    /// WMI (DMTF) string like "20260920143000.123456-000" — local wall-clock
    /// time followed by the UTC offset in minutes. Returns default when it
    /// is neither, so callers treat the point as "age unknown".
    /// </summary>
    internal static DateTime ParseCreationTime(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return default;

        var epoch = System.Text.RegularExpressions.Regex.Match(raw, @"^/Date\((-?\d+)");
        if (epoch.Success)
            return long.TryParse(epoch.Groups[1].Value, out var ms)
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime
                : default;

        return raw.Length >= 14 && DateTime.TryParseExact(
                raw[..14], "yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var wmi)
            ? wmi
            : default;
    }

    /// <summary>
    /// Safety net for actions that change system settings (network stack
    /// reset, SFC/DISM repair, Optimize): when "Create a restore point before
    /// system repairs" is on, makes one first. Skipped when a restore point
    /// from the last 24 hours already exists — Windows won't create more than
    /// one automatic restore point per 24 hours anyway.
    ///
    /// Never blocks the action it protects: if System Restore is switched
    /// off for the system drive, or the checkpoint fails, that is logged and
    /// the action carries on. Cancellation still propagates.
    /// </summary>
    /// <returns>True when a restore point from the last 24 hours exists (new or already there).</returns>
    public static async Task<bool> EnsureBeforeAsync(
        string reason, IProgress<TaskProgress>? progress = null, CancellationToken ct = default)
    {
        if (!SettingsService.Current.AutoRestorePoint)
            return false;

        try
        {
            var newest = (await ListAsync(ct)).FirstOrDefault(); // ListAsync orders newest-first
            if (newest is not null && DateTime.Now - newest.CreationTime < TimeSpan.FromHours(24))
            {
                Log.Info($"A restore point from the last 24 hours already exists ({newest.CreationTime:yyyy-MM-dd HH:mm}) — not creating another before {reason}.");
                return true;
            }

            progress?.Report(TaskProgress.Busy("Creating a restore point first..."));
            Log.Info($"Creating a restore point before {reason}...");

            // Plain ASCII on purpose: this text travels through powershell.exe's command line.
            var ps = await ProcessRunner.RunPowerShellAsync(
                $"Checkpoint-Computer -Description 'Solvent - before {reason}' -RestorePointType MODIFY_SETTINGS", ct);

            if (ps.Succeeded)
            {
                Log.Success("Restore point created.");
                return true;
            }

            Log.Warning("Could not create a restore point (System Restore may be turned off for the system drive) — continuing without one.");
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning($"Could not create a restore point ({ex.Message}) — continuing without one.");
            return false;
        }
    }

    /// <summary>Opens the built-in System Restore wizard so the user can pick and apply a restore point themselves.</summary>
    public static void OpenRestoreWizard()
    {
        Log.Info("Opening System Restore...");
        ProcessRunner.LaunchDetached("rstrui.exe");
    }
}
