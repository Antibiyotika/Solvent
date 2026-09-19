using System.Diagnostics;
using SolventUI.Models;

namespace SolventUI.Services;

// ---------------------------------------------------------------------
// Two additions to the Performance page that used to be missing:
//
// 1. Optimize used to always run a full `defrag /O` on the system drive,
//    even on an SSD. Modern Windows defrag.exe is media-aware and mostly
//    does the right thing on its own, but that's an internal heuristic
//    the UI never surfaced — the user just saw "defragmenting", which
//    reads as exactly the operation you're told never to run on an SSD.
//    This service detects the media type up front and explicitly runs
//    Optimize-Volume -ReTrim (SSD) or defrag /O (HDD), so the log and
//    the Performance page can say plainly which one happened and why.
//
// 2. "Free Up Memory" — trims the working sets of ordinary background
//    processes via the documented EmptyWorkingSet API. This does not
//    kill or suspend anything; it just asks Windows to page idle memory
//    out to the standby list, the same thing Windows itself does over
//    time. Processes get pages back automatically the moment they touch
//    them again, so this is safe to run repeatedly.
// ---------------------------------------------------------------------

public static class PerformanceBoostService
{
    private static readonly LogService Log = LogService.Instance;

    /// <summary>
    /// Reads the physical media type behind a drive letter via the Storage
    /// module (Get-PhysicalDisk), the same source Windows' own Optimize
    /// Drives control panel uses. Falls back to Unknown on VMs, exotic
    /// storage, or when PowerShell's Storage module isn't available.
    /// </summary>
    public static async Task<DiskMediaKind> DetectMediaKindAsync(string driveLetter, CancellationToken ct = default)
    {
        try
        {
            var letter = driveLetter.TrimEnd(':', '\\');
            var ps = await ProcessRunner.RunPowerShellAsync(
                $"(Get-Partition -DriveLetter '{letter}' | Get-Disk | Get-PhysicalDisk | Select-Object -First 1 -ExpandProperty MediaType)",
                ct);

            if (!ps.Succeeded) return DiskMediaKind.Unknown;
            var text = ps.StdOut.Trim();
            if (text.Equals("SSD", StringComparison.OrdinalIgnoreCase)) return DiskMediaKind.Ssd;
            if (text.Equals("HDD", StringComparison.OrdinalIgnoreCase)) return DiskMediaKind.Hdd;
            return DiskMediaKind.Unknown;
        }
        catch
        {
            return DiskMediaKind.Unknown;
        }
    }

    /// <summary>
    /// Runs the right optimization for the drive's media type: a TRIM-only
    /// retrim for SSDs (seconds, no wear-adding rewrite passes) or a full
    /// defragmentation for spinning disks. Unknown media falls back to
    /// defrag.exe, which performs its own SSD detection internally, so this
    /// is never worse than the previous unconditional behavior — it's just
    /// explicit and logged instead of silent.
    /// </summary>
    public static async Task<DriveOptimizeResult> OptimizeDriveAsync(
        string driveLetter, IProgress<TaskProgress>? progress = null, CancellationToken ct = default)
    {
        var letter = driveLetter.TrimEnd(':', '\\');
        progress?.Report(TaskProgress.Busy("Detecting drive type..."));
        var kind = await DetectMediaKindAsync(letter, ct);

        switch (kind)
        {
            case DiskMediaKind.Ssd:
                progress?.Report(TaskProgress.Busy($"SSD detected — running TRIM retrim on {letter}: (no full defrag)..."));
                Log.Info($"Optimize: {letter}: is an SSD — running Optimize-Volume -ReTrim instead of a full defrag.");
                var retrim = await ProcessRunner.RunPowerShellAsync($"Optimize-Volume -DriveLetter '{letter}' -ReTrim -Verbose", ct);
                return new DriveOptimizeResult(kind, retrim.Succeeded, letter);

            case DiskMediaKind.Hdd:
                progress?.Report(TaskProgress.Busy($"HDD detected — defragmenting {letter}: (this can take a while)..."));
                Log.Info($"Optimize: {letter}: is a hard disk — running a full defragmentation.");
                var defragHdd = await ProcessRunner.RunAsync("defrag.exe", $"{letter}: /O", ct);
                return new DriveOptimizeResult(kind, defragHdd.Succeeded, letter);

            default:
                progress?.Report(TaskProgress.Busy($"Optimizing {letter}: (Windows will choose the right method for this drive)..."));
                Log.Info($"Optimize: couldn't determine {letter}:'s media type — using defrag.exe, which detects it internally.");
                var defragAuto = await ProcessRunner.RunAsync("defrag.exe", $"{letter}: /O", ct);
                return new DriveOptimizeResult(kind, defragAuto.Succeeded, letter);
        }
    }

    /// <summary>
    /// Process names that are never trimmed: doing so is either a no-op
    /// (kernel-owned), pointless (already minimal), or capable of causing a
    /// visible stutter in something the user is actively looking at (the
    /// window manager, the shell, this app itself).
    /// </summary>
    private static readonly HashSet<string> ExcludedProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Idle", "System", "Registry", "Memory Compression", "Secure System",
        "csrss", "wininit", "winlogon", "smss", "services", "lsass",
        "dwm", "explorer", "Solvent",
    };

    /// <summary>
    /// Trims the working set of eligible running processes. Safe by design:
    /// EmptyWorkingSet never terminates or suspends a process, only pages
    /// out memory it isn't actively touching, so nothing is lost and
    /// nothing crashes if a trim is refused (many system processes will
    /// refuse it outright under least privilege — that's expected and
    /// counted as skipped, not as an error).
    /// </summary>
    public static Task<MemoryTrimResult> FreeUpMemoryAsync(IProgress<TaskProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            Log.Info("Free Up Memory: trimming working sets of background processes...");
            var before = GetAvailableBytes();

            var processes = Process.GetProcesses();
            var trimmed = 0;
            var total = processes.Length;
            var step = 0;

            foreach (var process in processes)
            {
                ct.ThrowIfCancellationRequested();
                step++;
                if (step % 10 == 0)
                    progress?.Report(TaskProgress.Step(step, total, $"Trimming processes ({step}/{total})..."));

                try
                {
                    if (ExcludedProcessNames.Contains(process.ProcessName)) continue;
                    if (process.Id == Environment.ProcessId) continue;

                    if (NativeMethods.EmptyWorkingSet(process.Handle))
                        trimmed++;
                }
                catch
                {
                    // Access denied (protected/system process) or the process
                    // exited mid-loop — both are normal and just skipped.
                }
                finally
                {
                    process.Dispose();
                }
            }

            // Give the memory manager a moment to update its counters before
            // reading "after" — GlobalMemoryStatusEx can lag a working-set
            // trim by a beat.
            Thread.Sleep(400);
            var after = GetAvailableBytes();
            var freed = Math.Max(0, after - before);

            progress?.Report(TaskProgress.Step(total, total, "Free Up Memory finished."));
            Log.Success($"Free Up Memory: trimmed {trimmed} process(es), ~{SizeFormat.Format(freed)} returned to the standby list.");
            return new MemoryTrimResult(trimmed, freed);
        }, ct);

    private static long GetAvailableBytes()
    {
        try
        {
            var status = new NativeMethods.MEMORYSTATUSEX
            {
                dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>(),
            };
            return NativeMethods.GlobalMemoryStatusEx(ref status) ? (long)status.ullAvailPhys : 0;
        }
        catch
        {
            return 0;
        }
    }
}
