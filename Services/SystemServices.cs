using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Win32;
using SolventUI.Models;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace SolventUI.Services;

// ---------------------------------------------------------------------
// Everything that isn't a "run a repair/cleanup task" (that lives in
// MaintenanceServices.cs): process execution, read-only system/hardware
// state, the event log, live resource monitoring, and every small piece
// of app infrastructure (log feed, confirmations, settings, theme,
// localization, startup manager, schedule manager). All of it used to
// live in five separate files; none of them is more than a couple
// hundred lines or has more than one or two things going on, so keeping
// them apart just meant more files to open to see the whole picture of
// "what does Solvent know about this PC and itself". One file now.
// ---------------------------------------------------------------------

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Runs an external command and captures its output — the C# equivalent
/// of RunCommandCaptured() in sysinfo.cpp. Used for sfc, DISM, netsh,
/// powercfg, schtasks, PowerShell (Get-PhysicalDisk, Checkpoint-Computer...).
/// Optionally writes to the process's stdin once, then closes it — needed
/// for the handful of legacy console tools (chkdsk) that ask an
/// interactive Y/N question instead of taking a command-line switch.
/// </summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        CancellationToken ct = default,
        string? stdInput = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdInput != null,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        if (stdInput != null)
        {
            await process.StandardInput.WriteLineAsync(stdInput);
            process.StandardInput.Close();
        }

        var stdOutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stdErrTask = process.StandardError.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;

        return new ProcessResult(process.ExitCode, stdOut, stdErr);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { /* best effort — process may have exited between the checks */ }
    }

    public static Task<ProcessResult> RunPowerShellAsync(string script, CancellationToken ct = default) =>
        RunAsync("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"",
            ct);

    /// <summary>Starts a program the normal (visible/shell) way and doesn't wait for it — for opening tools like rstrui.exe, mdsched.exe, or a generated report in the browser.</summary>
    public static void LaunchDetached(string fileName, string arguments = "")
    {
        try
        {
            Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Could not launch {fileName}: {ex.Message}");
        }
    }
}

public sealed record SystemInfoResult(
    string ProductName,
    string VersionLabel,   // "Windows 11" / "Windows 10"
    int BuildNumber,
    bool IsAdmin,
    string TotalRam,
    string SystemDrive);

/// <summary>
/// C# port of sysinfo.cpp's SystemInfo gathering: Windows 10 vs 11 via
/// build number (Win11 = build >= 22000), product name from the registry,
/// RAM total, and elevation state.
/// </summary>
public static class SystemInfoService
{
    public static SystemInfoResult GetSystemInfo()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");

        var productName = key?.GetValue("ProductName") as string ?? "Windows";
        var buildStr = key?.GetValue("CurrentBuildNumber") as string ?? "0";
        int.TryParse(buildStr, out var build);

        // Windows 11 keeps "Windows 10" in ProductName up through build 22000+,
        // so build number is the reliable signal (same rule tasks.cpp used).
        var versionLabel = build >= 22000 ? "Windows 11" : "Windows 10";

        var isAdmin = IsRunningAsAdmin();
        var ram = ResourceMonitorService.GetTotalRamFormatted();
        var systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";

        return new SystemInfoResult(productName, versionLabel, build, isAdmin, ram, systemDrive);
    }

    public static bool IsRunningAsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Relaunches Solvent elevated (UAC prompt) and closes this instance — for the "Restart as Administrator" banner when a repair needs elevation Solvent doesn't have.</summary>
    public static void RestartElevated()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (exePath is null) return;
            Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User declined the UAC prompt — stay running unelevated.
        }
    }
}

/// <summary>
/// Live "how is this PC doing right now" numbers: CPU load, RAM used vs
/// total, and free space per fixed drive. Backs the Dashboard's live
/// gauges (polled on a timer) and feeds the Diagnostics health check's
/// "RAM is nearly full" / "system drive nearly full" signals — real,
/// continuously-updating figures rather than the one-shot static totals
/// Solvent used to show.
/// </summary>
public static class ResourceMonitorService
{
    private static PerformanceCounter? _cpuCounter;
    private static bool _cpuCounterFailed;

    public static ResourceSnapshot GetSnapshot()
    {
        var cpu = GetCpuPercent();
        var (ramUsed, ramTotal) = GetRamGigabytes();
        var drives = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => new DriveSnapshot(
                d.Name.TrimEnd('\\'),
                d.TotalSize,
                d.TotalSize - d.AvailableFreeSpace))
            .ToList();

        return new ResourceSnapshot(cpu, ramUsed, ramTotal, drives);
    }

    private static double GetCpuPercent()
    {
        if (_cpuCounterFailed) return -1;
        try
        {
            _cpuCounter ??= new PerformanceCounter("Processor", "% Processor Time", "_Total");
            // First read after creating the counter is always 0 — a harmless
            // quirk of PerformanceCounter, not a real 0% reading. The Dashboard
            // polls every couple of seconds so this only shows once at startup.
            return Math.Clamp(_cpuCounter.NextValue(), 0, 100);
        }
        catch
        {
            _cpuCounterFailed = true; // perf counters can be disabled/corrupted on some systems — degrade quietly
            return -1;
        }
    }

    private static (double UsedGb, double TotalGb) GetRamGigabytes()
    {
        try
        {
            var status = new MEMORYSTATUSEX();
            status.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
            if (GlobalMemoryStatusEx(ref status))
            {
                var totalGb = status.ullTotalPhys / 1024d / 1024d / 1024d;
                var availGb = status.ullAvailPhys / 1024d / 1024d / 1024d;
                return (totalGb - availGb, totalGb);
            }
        }
        catch { /* fall through */ }
        return (0, 0);
    }

    public static string GetTotalRamFormatted()
    {
        var (_, total) = GetRamGigabytes();
        return total <= 0 ? "—" : $"{total:0.#} GB";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}

public sealed record CrashSummary(int UnexpectedShutdowns, int AppCrashes, int CriticalSystemErrors, DateTime? LastEventTime);

/// <summary>
/// Reads the Windows "System" and "Application" event logs to answer the
/// question a user actually has — "has this PC been crashing?" — without
/// making them open Event Viewer themselves.
///
///   • Event ID 41 (Kernel-Power)  → unexpected shutdown / power loss / hard hang
///   • Event ID 1001 (BugCheck)    → a real BSOD was recorded
///   • Application log "Error"     → application crashes (e.g. .NET/Win32 fault)
///   • System log "Critical"       → driver/service level critical failures
/// </summary>
public static class EventLogService
{
    public static Task<CrashSummary> GetRecentCrashSummaryAsync(int lookbackDays = 7) =>
        Task.Run(() =>
        {
            var since = DateTime.Now.AddDays(-lookbackDays);
            var unexpectedShutdowns = 0;
            var criticalSystemErrors = 0;
            var appCrashes = 0;
            DateTime? lastEvent = null;

            void Scan(string logName, Action<EventRecord> onRecord)
            {
                try
                {
                    var query = new EventLogQuery(logName, PathType.LogName,
                        "*[System[(Level=1 or Level=2) or (EventID=41)]]");
                    using var reader = new EventLogReader(query);
                    EventRecord? rec;
                    var scanned = 0;
                    // Cap the scan so a huge log can't hang the UI — most-recent-first,
                    // and we only care about the last `lookbackDays` anyway.
                    while (scanned < 500 && (rec = reader.ReadEvent()) != null)
                    {
                        using (rec)
                        {
                            scanned++;
                            if (rec.TimeCreated is null || rec.TimeCreated < since)
                                break; // events come back newest-first; we've walked past the window
                            onRecord(rec);
                            if (lastEvent is null || rec.TimeCreated > lastEvent)
                                lastEvent = rec.TimeCreated;
                        }
                    }
                }
                catch
                {
                    // Event log may be inaccessible without admin on some systems — degrade quietly.
                }
            }

            Scan("System", rec =>
            {
                if (rec.Id == 41 || rec.Id == 6008)
                    unexpectedShutdowns++;
                else if (rec.Id == 1001 || rec.Level == 1) // 1001 = BugCheck, Level 1 = Critical
                    criticalSystemErrors++;
            });

            Scan("Application", rec =>
            {
                if (rec.ProviderName is "Application Error" or ".NET Runtime" || rec.Level == 2)
                    appCrashes++;
            });

            return new CrashSummary(unexpectedShutdowns, appCrashes, criticalSystemErrors, lastEvent);
        });
}

public sealed record ProblemDevice(string Name, string DeviceId, int ErrorCode, string ErrorText);

/// <summary>
/// Queries Win32_PnPEntity for devices Device Manager would show with a
/// yellow warning icon (ConfigManagerErrorCode != 0) — the same data
/// Device Manager itself reads, just surfaced without making the user
/// dig through it manually.
/// </summary>
public static class DeviceHealthService
{
    // https://learn.microsoft.com/windows-hardware/drivers/install/cm-error-codes
    private static readonly Dictionary<int, string> ErrorText = new()
    {
        [1] = "Device is not configured correctly.",
        [3] = "Driver may be corrupted, or the system is low on memory.",
        [10] = "Device cannot start.",
        [12] = "Not enough free resources (IRQ/memory) available.",
        [14] = "Device needs a restart to work correctly.",
        [16] = "Windows cannot identify all resources this device uses.",
        [18] = "Drivers need to be reinstalled.",
        [19] = "Registry settings for this device are corrupted or in conflict.",
        [21] = "Windows is removing this device.",
        [22] = "Device is disabled.",
        [24] = "Device is not present, not working, or missing drivers.",
        [28] = "Drivers for this device are not installed.",
        [31] = "Device is not working properly — Windows cannot load the drivers.",
        [32] = "Driver service is disabled.",
        [37] = "Windows cannot initialize the device driver.",
        [39] = "Driver is missing or corrupted.",
        [41] = "Windows successfully loaded the driver but cannot find the device.",
        [43] = "Windows has stopped this device because it reported problems.",
        [44] = "Application or service has shut down this hardware device.",
        [45] = "Device is not connected to the computer.",
        [48] = "Software for this device has been blocked from starting.",
    };

    public static Task<List<ProblemDevice>> GetProblemDevicesAsync() =>
        Task.Run(() =>
        {
            var results = new List<ProblemDevice>();
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, DeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity " +
                    "WHERE ConfigManagerErrorCode != 0");

                foreach (ManagementObject device in searcher.Get())
                {
                    var code = Convert.ToInt32(device["ConfigManagerErrorCode"] ?? 0);
                    if (code == 0) continue;

                    var name = device["Name"] as string ?? "Unknown device";
                    var id = device["DeviceID"] as string ?? "";
                    var text = ErrorText.TryGetValue(code, out var t) ? t : $"Windows reported error code {code}.";
                    results.Add(new ProblemDevice(name, id, code, text));
                }
            }
            catch
            {
                // WMI unavailable/blocked — return whatever we have (possibly empty).
            }
            return results;
        });

    /// <summary>
    /// Asks Windows to re-scan for hardware changes, the same action as
    /// Device Manager's "Scan for hardware changes" toolbar button. This
    /// re-enumerates the bus and can clear transient "device not present"
    /// / "device cannot start" states without a reboot.
    /// </summary>
    public static Task<ProcessResult> RescanHardwareAsync(CancellationToken ct = default) =>
        ProcessRunner.RunAsync("pnputil.exe", "/scan-devices", ct);
}

/// <summary>
/// App-wide activity log. Singleton so every page (Dashboard, Repair &amp;
/// Security, Performance, Cleanup...) writes to the same feed, exactly
/// like the single RichEdit control in the C++ build.
/// </summary>
public sealed class LogService
{
    public static LogService Instance { get; } = new();

    public ObservableCollection<LogEntry> Entries { get; } = new();

    private LogService() { }

    public void Log(LogKind kind, string message)
    {
        void Add() => Entries.Add(new LogEntry { Kind = kind, Message = message });

        if (Application.Current?.Dispatcher.CheckAccess() == true)
            Add();
        else
            Application.Current?.Dispatcher.Invoke(Add);
    }

    public void Info(string message) => Log(LogKind.Info, message);
    public void Success(string message) => Log(LogKind.Success, message);
    public void Warning(string message) => Log(LogKind.Warning, message);
    public void Error(string message) => Log(LogKind.Error, message);

    public void Clear() => Application.Current?.Dispatcher.Invoke(Entries.Clear);
}

/// <summary>
/// Confirmation prompts for actions that make disruptive or hard-to-undo
/// system changes (deleting files, resetting the network stack, etc.).
/// Read-only checks and purely protective actions (creating a restore
/// point, enabling Defender, scanning for malware, scheduling a memory
/// test) don't go through this — only ones a user could regret running
/// by accident.
/// </summary>
public static class ConfirmationService
{
    public static bool Ask(string title, string message)
    {
        var result = System.Windows.MessageBox.Show(
            message,
            title,
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);

        return result == System.Windows.MessageBoxResult.Yes;
    }
}

/// <summary>
/// C# port of schedule_manager.cpp: creates/removes a Task Scheduler
/// entry via schtasks.exe that relaunches this app with an `/autoclean`
/// flag, running with highest privileges on the chosen day/time.
/// </summary>
public static class ScheduleManagerService
{
    private const string TaskName = "SolventAutoClean";

    public static async Task ApplyAsync(ScheduleSettings settings)
    {
        if (!settings.IsEnabled)
        {
            await RemoveAsync();
            return;
        }

        var exePath = Environment.ProcessPath ?? "Solvent.exe";
        var day = settings.DayOfWeek.ToString().Substring(0, 3).ToUpperInvariant();
        var time = settings.TimeOfDay.ToString(@"hh\:mm");
        var rl = settings.RunHighestPrivileges ? "HIGHEST" : "LIMITED";

        var args = $"/Create /F /SC WEEKLY /D {day} /ST {time} /RL {rl} " +
                   $"/TN \"{TaskName}\" /TR \"\\\"{exePath}\\\" /autoclean\"";

        LogService.Instance.Info($"Schedule: creating weekly task ({day} {time})...");
        var result = await ProcessRunner.RunAsync("schtasks.exe", args);

        if (result.Succeeded)
            LogService.Instance.Success("Automatic cleanup schedule saved.");
        else
            LogService.Instance.Error($"Could not create the scheduled task (exit code {result.ExitCode}).");
    }

    public static async Task RemoveAsync()
    {
        var result = await ProcessRunner.RunAsync("schtasks.exe", $"/Delete /F /TN \"{TaskName}\"");
        if (result.Succeeded)
            LogService.Instance.Info("Automatic cleanup schedule removed.");
    }

    public static async Task<bool> ExistsAsync()
    {
        var result = await ProcessRunner.RunAsync("schtasks.exe", $"/Query /TN \"{TaskName}\"");
        return result.Succeeded;
    }
}

/// <summary>
/// C# port of startup_manager.cpp: reads/writes HKCU Run entries and the
/// user's Startup folder shortcuts. "Disabling" a Run entry moves it to
/// a backup key instead of deleting it, so it can be restored.
/// </summary>
public static class StartupManagerService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunDisabledKeyPath = @"Software\Solvent\DisabledRun";

    public static List<StartupItem> GetStartupItems()
    {
        var items = new List<StartupItem>();

        using (var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath))
        {
            if (runKey != null)
            {
                foreach (var name in runKey.GetValueNames())
                {
                    items.Add(new StartupItem
                    {
                        Name = name,
                        Command = runKey.GetValue(name) as string ?? string.Empty,
                        Source = StartupSource.RegistryRun,
                        IsEnabled = true
                    });
                }
            }
        }

        using (var disabledKey = Registry.CurrentUser.OpenSubKey(RunDisabledKeyPath))
        {
            if (disabledKey != null)
            {
                foreach (var name in disabledKey.GetValueNames())
                {
                    items.Add(new StartupItem
                    {
                        Name = name,
                        Command = disabledKey.GetValue(name) as string ?? string.Empty,
                        Source = StartupSource.RegistryRun,
                        IsEnabled = false
                    });
                }
            }
        }

        var startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        if (Directory.Exists(startupFolder))
        {
            foreach (var file in Directory.GetFiles(startupFolder, "*.lnk"))
            {
                items.Add(new StartupItem
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    Command = ResolveShortcutTarget(file),
                    Source = StartupSource.StartupFolder,
                    IsEnabled = true
                });
            }
        }

        return items.OrderBy(i => i.Name).ToList();
    }

    public static void SetEnabled(StartupItem item, bool enabled)
    {
        if (item.Source != StartupSource.RegistryRun)
            return; // Startup-folder items are toggled by moving the .lnk — not implemented here to keep it safe.

        using var enabledKey = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        using var disabledKey = Registry.CurrentUser.CreateSubKey(RunDisabledKeyPath);

        if (enabled)
        {
            var value = disabledKey?.GetValue(item.Name) as string ?? item.Command;
            enabledKey?.SetValue(item.Name, value);
            disabledKey?.DeleteValue(item.Name, throwOnMissingValue: false);
        }
        else
        {
            var value = enabledKey?.GetValue(item.Name) as string ?? item.Command;
            disabledKey?.SetValue(item.Name, value);
            enabledKey?.DeleteValue(item.Name, throwOnMissingValue: false);
        }
    }

    public static void Remove(StartupItem item)
    {
        if (item.Source == StartupSource.RegistryRun)
        {
            using var enabledKey = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            using var disabledKey = Registry.CurrentUser.CreateSubKey(RunDisabledKeyPath);
            enabledKey?.DeleteValue(item.Name, throwOnMissingValue: false);
            disabledKey?.DeleteValue(item.Name, throwOnMissingValue: false);
        }
        else
        {
            var startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            var lnk = Path.Combine(startupFolder, item.Name + ".lnk");
            if (File.Exists(lnk))
                File.Delete(lnk);
        }
    }

    /// <summary>
    /// Best-effort enrichment: resolves each item's real executable, flags
    /// ones whose file no longer exists (safe, verified-safe removals — not
    /// a guess), and reads Authenticode signature status for the rest. All
    /// items are checked in a single PowerShell call regardless of count, so
    /// refreshing a list of 20+ entries doesn't spawn 20+ processes. Never
    /// throws — items simply keep their "Unknown" defaults if this fails
    /// entirely (e.g. PowerShell unavailable).
    /// </summary>
    public static async Task EnrichWithPublisherInfoAsync(IEnumerable<StartupItem> items, CancellationToken ct = default)
    {
        var paths = new Dictionary<StartupItem, string?>();
        foreach (var item in items)
            paths[item] = ResolveExecutablePath(item.Command);

        foreach (var (item, path) in paths)
        {
            item.FileExists = path is not null && File.Exists(path);
            if (!item.FileExists)
                item.SignatureStatus = StartupSignatureStatus.FileMissing;
        }

        var toCheck = paths.Where(kv => kv.Value is not null && File.Exists(kv.Value)).ToList();
        if (toCheck.Count == 0) return;

        try
        {
            var pathsLiteral = string.Join(",", toCheck.Select(kv => $"'{kv.Value!.Replace("'", "''")}'"));
            var script =
                $"@({pathsLiteral}) | ForEach-Object {{ " +
                "$sig = Get-AuthenticodeSignature -FilePath $_; " +
                "$org = $null; " +
                "if ($sig.SignerCertificate -and $sig.SignerCertificate.Subject -match 'O=([^,]+)') { $org = $matches[1] } " +
                "[PSCustomObject]@{ Path = $_; Status = $sig.Status.ToString(); Publisher = $org } " +
                "} | ConvertTo-Json -Compress";

            var ps = await ProcessRunner.RunPowerShellAsync(script, ct);
            if (!ps.Succeeded || string.IsNullOrWhiteSpace(ps.StdOut)) return;

            var json = ps.StdOut.Trim();
            using var doc = JsonDocument.Parse(json.StartsWith('[') ? json : $"[{json}]");
            var byPath = new Dictionary<string, (string Status, string? Publisher)>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var path = element.TryGetProperty("Path", out var p) ? p.GetString() : null;
                if (path is null) continue;
                var status = element.TryGetProperty("Status", out var s) ? s.GetString() ?? "Unknown" : "Unknown";
                var publisher = element.TryGetProperty("Publisher", out var pub) && pub.ValueKind == JsonValueKind.String
                    ? pub.GetString() : null;
                byPath[path] = (status, publisher);
            }

            foreach (var (item, path) in toCheck)
            {
                if (path is null || !byPath.TryGetValue(path, out var info)) continue;
                item.Publisher = info.Publisher;
                item.SignatureStatus = info.Status switch
                {
                    "Valid" => StartupSignatureStatus.Valid,
                    "NotSigned" => StartupSignatureStatus.NotSigned,
                    _ => StartupSignatureStatus.Invalid,
                };
            }
        }
        catch
        {
            // Signature info is a bonus, not a requirement — items keep their file-exists result either way.
        }
    }

    /// <summary>
    /// Best-effort path resolution for a Run-key command line or a
    /// (now-resolved, see <see cref="ResolveShortcutTarget"/>) startup-folder
    /// target. An unquoted path containing spaces with no quoting at all is
    /// genuinely ambiguous ("C:\Program Files\App.exe" vs "...App.exe
    /// --flag") — this takes the first whitespace-delimited token, which
    /// covers quoted paths and the common short/no-space-path case.
    /// </summary>
    private static string? ResolveExecutablePath(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var trimmed = command.Trim();

        string candidate;
        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            candidate = end > 0 ? trimmed[1..end] : trimmed.Trim('"');
        }
        else
        {
            var spaceIndex = trimmed.IndexOf(' ');
            candidate = spaceIndex > 0 ? trimmed[..spaceIndex] : trimmed;
        }

        candidate = Environment.ExpandEnvironmentVariables(candidate.Trim());
        if (candidate.Length == 0) return null;

        // A bare filename with no path (e.g. "rundll32.exe") resolves via
        // System32 instead of failing the file-exists check outright.
        if (!candidate.Contains('\\') && !candidate.Contains('/'))
        {
            var inSystem32 = Path.Combine(Environment.SystemDirectory, candidate);
            if (File.Exists(inSystem32)) return inSystem32;
        }

        return candidate;
    }

    // Resolves a .lnk's real target via the WScript.Shell COM automation
    // object (always present on Windows) using late-bound reflection —
    // avoids hand-rolled IShellLink P/Invoke struct marshaling entirely.
    // Falls back to the .lnk path itself if COM automation isn't available
    // or the shortcut can't be read, same as the previous stub behavior.
    private static string ResolveShortcutTarget(string lnkPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return lnkPath;

            var shell = Activator.CreateInstance(shellType);
            if (shell is null) return lnkPath;

            var shortcut = shellType.InvokeMember("CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            if (shortcut is null) return lnkPath;

            var target = shortcut.GetType().InvokeMember("TargetPath",
                System.Reflection.BindingFlags.GetProperty, null, shortcut, null) as string;

            return string.IsNullOrWhiteSpace(target) ? lnkPath : target;
        }
        catch
        {
            return lnkPath;
        }
    }
}

/// <summary>
/// Adds or removes Solvent itself under the name "Solvent" in the exact
/// same HKCU Run key <see cref="StartupManagerService"/> already manages
/// for every other app — so once enabled from the Settings page, Solvent
/// also shows up (and can be toggled or removed) right in the Startup
/// Programs list, like any other entry, since it's the same key.
/// </summary>
public static class StartupLaunchService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Solvent";

    /// <summary>True if an HKCU Run entry named "Solvent" currently exists.</summary>
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is string;
    }

    /// <summary>
    /// Adds/removes the Run entry, pointing at the currently running exe.
    /// Best-effort: failures (locked-down policy, read-only key) are logged
    /// rather than thrown, since this runs from a Settings "Save" click and
    /// shouldn't block saving the rest of the page's preferences.
    /// </summary>
    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null)
                return;

            if (enabled)
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                {
                    LogService.Instance.Warning("Could not resolve the running exe path for the startup entry.");
                    return;
                }
                key.SetValue(ValueName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"Could not update the Windows startup entry: {ex.Message}");
        }
    }
}

/// <summary>
/// Persists <see cref="AppSettings"/> as a small JSON file outside the
/// install folder, at <c>%AppData%\Solvent\settings.json</c> — the same
/// "Data" folder the installer is expected to create under the user's
/// AppData at setup time. Keeping this separate from the app's own
/// install directory (Program Files, or wherever the zip is unpacked)
/// means settings survive an app update/reinstall, and the folder is
/// always writable without admin rights (unlike Program Files).
/// </summary>
public static class SettingsService
{
    private static readonly string DataFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Solvent");

    private static readonly string SettingsFilePath = Path.Combine(DataFolder, "settings.json");

    // JsonStringEnumConverter writes ThemeMode as "Dark"/"Light" instead of
    // 0/1 — keeps settings.json human-readable for anyone (installer,
    // support, the user themself) who opens it directly.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static AppSettings? _current;

    /// <summary>Currently loaded settings. Loads from disk on first access.</summary>
    public static AppSettings Current => _current ??= Load();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded != null)
                {
                    _current = loaded;
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            // Corrupt or unreadable settings file — fall back to defaults
            // rather than crashing the whole app on startup.
            LogService.Instance.Warning($"Could not read settings.json, using defaults: {ex.Message}");
        }

        var defaults = new AppSettings();
        _current = defaults;
        Save(defaults); // seed the file so the Data folder exists from first run
        return defaults;
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsFilePath, json);
            _current = settings;
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Could not save settings: {ex.Message}");
        }
    }
}

/// <summary>
/// Applies a <see cref="AppThemeMode"/> both to WPF-UI's own native
/// controls (title bar, buttons, NavigationView chrome — via
/// <see cref="ApplicationThemeManager"/>) and to Solvent's own palette
/// (Themes/Colors.xaml vs Themes/ColorsLight.xaml). The custom palette is
/// swapped by replacing the merged dictionary in
/// <c>Application.Current.Resources</c>; every page/control that reads
/// those brushes with <c>DynamicResource</c> (not <c>StaticResource</c>)
/// picks up the change immediately, with no restart needed.
/// </summary>
public static class ThemeService
{
    /// <summary>Solvent's original green — used whenever a stored/typed hex fails to parse.</summary>
    private const string DefaultAccentHex = "#22C55E";

    /// <summary>
    /// Curated accent choices shown as swatches on the Settings page. Only
    /// the base color is stored (in <see cref="AppSettings.AccentColorHex"/>)
    /// — the matching hover shade is always derived at apply-time via
    /// <see cref="DeriveHoverColor"/> rather than hand-picked per preset, so
    /// it stays correctly light-on-dark/dark-on-light automatically.
    /// </summary>
    public static readonly IReadOnlyList<string> AccentPresets = new[]
    {
        "#22C55E", // Green (default)
        "#3B82F6", // Blue
        "#8B5CF6", // Purple
        "#F97316", // Orange
        "#F43F5E", // Rose
        "#14B8A6", // Teal
    };

    public static void Apply(AppThemeMode mode)
    {
        // "System" isn't a real palette by itself — resolve it to whichever
        // of Dark/Light Windows is currently set to, then apply that.
        var resolved = mode == AppThemeMode.System ? DetectSystemTheme() : mode;

        var theme = resolved == AppThemeMode.Light ? ApplicationTheme.Light : ApplicationTheme.Dark;
        var source = resolved == AppThemeMode.Light ? "Themes/ColorsLight.xaml" : "Themes/Colors.xaml";

        SwapMergedDictionary(source, "Colors");
        ApplyAccentInternal(SettingsService.Current.AccentColorHex, theme);
    }

    /// <summary>
    /// Re-applies just the accent (base + derived hover) over whatever
    /// Dark/Light palette is already active — used when the user picks a
    /// new swatch on the Settings page, without needing a full theme swap.
    /// </summary>
    public static void ApplyAccent(string hex)
    {
        var current = SettingsService.Current.ThemeMode;
        var resolved = current == AppThemeMode.System ? DetectSystemTheme() : current;
        var theme = resolved == AppThemeMode.Light ? ApplicationTheme.Light : ApplicationTheme.Dark;
        ApplyAccentInternal(hex, theme);
    }

    private static void ApplyAccentInternal(string hex, ApplicationTheme theme)
    {
        var accent = ParseColorOrDefault(hex);
        var hover = DeriveHoverColor(accent, theme);

        var app = Application.Current;
        if (app != null)
        {
            // Set directly on Application.Resources (not inside a merged
            // dictionary) so these two keys win over whatever Colors.xaml/
            // ColorsLight.xaml just supplied — every DynamicResource lookup
            // for SolventAccentBrush/SolventAccentHoverBrush across the app
            // picks this up immediately, no restart needed.
            app.Resources["SolventAccentColor"] = accent;
            app.Resources["SolventAccentHoverColor"] = hover;
            app.Resources["SolventAccentBrush"] = new System.Windows.Media.SolidColorBrush(accent);
            app.Resources["SolventAccentHoverBrush"] = new System.Windows.Media.SolidColorBrush(hover);
        }

        ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, updateAccent: false);
        ApplicationAccentColorManager.Apply(accent, theme);
    }

    private static System.Windows.Media.Color ParseColorOrDefault(string? hex)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex))
                return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!;
        }
        catch
        {
            // Malformed/unknown string (hand-edited settings.json, old
            // build, etc.) — fall through to the default below.
        }
        return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(DefaultAccentHex)!;
    }

    /// <summary>
    /// A hover/pressed shade for any accent: lighter on the Dark palette
    /// (mirrors the original #22C55E → #3DDC73 pair), darker on Light
    /// (mirrors #16A34A → #15803D) — so a custom accent keeps the same
    /// "moves toward white on dark backgrounds, toward black on light
    /// backgrounds" contrast behavior the original two presets had.
    /// </summary>
    private static System.Windows.Media.Color DeriveHoverColor(System.Windows.Media.Color c, ApplicationTheme theme)
    {
        const double amount = 0.18;
        byte Blend(byte channel, byte target) => (byte)(channel + (target - channel) * amount);

        return theme == ApplicationTheme.Light
            ? System.Windows.Media.Color.FromRgb(Blend(c.R, 0), Blend(c.G, 0), Blend(c.B, 0))
            : System.Windows.Media.Color.FromRgb(Blend(c.R, 255), Blend(c.G, 255), Blend(c.B, 255));
    }

    /// <summary>
    /// Reads Windows' own Settings > Personalization > Colors > "Choose
    /// your default app mode" value straight from the registry — the same
    /// value Explorer and every other theme-aware app reads. Used to
    /// resolve <see cref="AppThemeMode.System"/>, both at startup and every
    /// time Windows reports the setting changed (see
    /// App.OnSystemThemeMaybeChanged, wired to SystemEvents.UserPreferenceChanged).
    /// </summary>
    public static AppThemeMode DetectSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int appsUseLightTheme)
                return appsUseLightTheme == 0 ? AppThemeMode.Dark : AppThemeMode.Light;
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"Could not read the Windows theme setting: {ex.Message}");
        }
        return AppThemeMode.Dark; // safe default if the key is missing or locked down by policy
    }

    private static void SwapMergedDictionary(string relativeSource, string matchToken)
    {
        var app = Application.Current;
        if (app == null)
            return;

        var dictionaries = app.Resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(
            d => d.Source != null && d.Source.OriginalString.Contains(matchToken));
        if (existing != null)
            dictionaries.Remove(existing);

        dictionaries.Add(new ResourceDictionary { Source = new Uri(relativeSource, UriKind.Relative) });
    }
}

/// <summary>
/// Swaps Themes/Strings.en.xaml ↔ Themes/Strings.tr.xaml in
/// <c>Application.Current.Resources</c>. XAML text bound with
/// <c>{DynamicResource Some_Key}</c> updates itself the moment the
/// dictionary is swapped — no restart needed. For the handful of places
/// that set text from code-behind instead (e.g. MainWindow's admin
/// badge), subscribe to <see cref="LanguageChanged"/> and re-read
/// <see cref="Get"/> when it fires.
/// </summary>
public sealed class LocalizationService
{
    public static LocalizationService Instance { get; } = new();

    public string CurrentLanguage { get; private set; } = "en";

    public event Action? LanguageChanged;

    private LocalizationService() { }

    /// <summary>Set the language at startup, before the main window is shown — no event raised.</summary>
    public void Initialize(string language) => Apply(language, raiseEvent: false);

    /// <summary>Change the language at runtime (e.g. from the Settings page) — raises <see cref="LanguageChanged"/>.</summary>
    public void SetLanguage(string language)
    {
        if (string.Equals(language, CurrentLanguage, StringComparison.OrdinalIgnoreCase))
            return;
        Apply(language, raiseEvent: true);
    }

    private void Apply(string language, bool raiseEvent)
    {
        var normalized = string.Equals(language, "tr", StringComparison.OrdinalIgnoreCase) ? "tr" : "en";
        var source = normalized == "tr" ? "Themes/Strings.tr.xaml" : "Themes/Strings.en.xaml";

        var app = Application.Current;
        if (app != null)
        {
            var dictionaries = app.Resources.MergedDictionaries;
            var existing = dictionaries.FirstOrDefault(
                d => d.Source != null && d.Source.OriginalString.Contains("Strings."));
            if (existing != null)
                dictionaries.Remove(existing);

            dictionaries.Add(new ResourceDictionary { Source = new Uri(source, UriKind.Relative) });
        }

        CurrentLanguage = normalized;
        if (raiseEvent)
            LanguageChanged?.Invoke();
    }

    /// <summary>Read a string resource from code-behind (for text not set via DynamicResource in XAML).</summary>
    public string Get(string key) => Application.Current?.TryFindResource(key) as string ?? key;
}
