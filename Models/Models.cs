using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SolventUI.Models;

// ---------------------------------------------------------------------
// All small data models used across the app, in one file — none of
// these are more than a few properties, so six separate files just
// meant six extra clicks to find a single record.
// ---------------------------------------------------------------------

/// <summary>A newer release found on GitHub — see <see cref="Services.UpdateService"/>.</summary>
public sealed class UpdateInfo
{
    public required Version Version { get; init; }
    public required string TagName { get; init; }
    public required string DownloadUrl { get; init; }

    /// <summary>URL of the release's <c>Solvent.exe.sha256</c> — the update is applied only if the download matches it.</summary>
    public required string ChecksumUrl { get; init; }
    public string ReleaseNotes { get; init; } = string.Empty;
    public string ReleaseUrl { get; init; } = string.Empty;
}

public enum AppThemeMode
{
    Dark,
    Light,

    /// <summary>
    /// Follows Windows' own "Choose your default app mode" setting, live —
    /// resolved by <see cref="Services.ThemeService.DetectSystemTheme"/> and
    /// re-applied automatically whenever Windows reports the setting
    /// changed (see App.OnSystemThemeMaybeChanged).
    /// </summary>
    System
}

/// <summary>
/// Persisted user preferences — theme, display language, and background/
/// startup behavior. Loaded/saved by <see cref="Services.SettingsService"/>
/// as JSON in the per-user data folder (see that class for the exact
/// path), kept deliberately tiny so it stays a stable, easy-to-hand-edit
/// file across app versions.
/// </summary>
public sealed class AppSettings
{
    public AppThemeMode ThemeMode { get; set; } = AppThemeMode.Dark;

    /// <summary>"en" or "tr" — see Themes/Strings.en.xaml / Strings.tr.xaml.</summary>
    public string Language { get; set; } = "en";

    /// <summary>
    /// When true, closing the main window hides it to a tray icon
    /// (<see cref="Services.TrayIconService"/>) instead of exiting the
    /// process — the tray icon's own "Exit" item is the only clean way
    /// out. When false, closing behaves exactly as it always has.
    /// </summary>
    public bool RunInBackground { get; set; }

    /// <summary>
    /// Mirrors an HKCU Run-key entry for Solvent itself, managed by
    /// <see cref="Services.StartupLaunchService"/>. Note this is a
    /// convenience toggle, not silent auto-elevation: because the app's
    /// manifest requests <c>requireAdministrator</c>, Windows will still
    /// show a UAC prompt at each logon before it actually launches.
    /// </summary>
    public bool LaunchAtStartup { get; set; }

    /// <summary>
    /// Hex color (e.g. "#22C55E") for Solvent's accent — buttons, links,
    /// progress bars, the accent brushes exposed as SolventAccentBrush/
    /// SolventAccentHoverBrush. Applied by <see cref="Services.ThemeService"/>
    /// on top of the Dark/Light palette, and to WPF-UI's own native accent
    /// via ApplicationAccentColorManager. Defaults to Solvent's original
    /// green so existing settings.json files (written before this option
    /// existed) still look exactly the same after upgrading.
    /// </summary>
    public string AccentColorHex { get; set; } = "#22C55E";
}

public enum IssueSeverity
{
    Info,       // FYI, nothing wrong
    Good,       // explicitly checked and healthy
    Warning,    // worth fixing, not urgent
    Critical    // actively hurting stability/security
}

/// <summary>
/// One finding from <see cref="Services.DiagnosticsService"/>'s health
/// check — e.g. "3 devices have driver errors", "Defender real-time
/// protection is off", "system drive is 4% free". Each issue optionally
/// carries a <see cref="FixActionId"/> that <c>DiagnosticsPage</c> maps
/// to a concrete one-click repair.
/// </summary>
public sealed class DiagnosticIssue
{
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public IssueSeverity Severity { get; init; } = IssueSeverity.Warning;

    /// <summary>Null when the issue has no automated fix (informational only).</summary>
    public string? FixActionId { get; init; }
    public string FixLabel { get; init; } = "Fix";
}

/// <summary>Shared "1.2 GB" style formatting — used by every model/service that reports a file or drive size.</summary>
public static class SizeFormat
{
    public static string Format(long bytes)
    {
        double b = bytes;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var i = 0;
        while (b >= 1024 && i < units.Length - 1)
        {
            b /= 1024;
            i++;
        }
        return $"{b:0.#} {units[i]}";
    }
}

/// <summary>One row in the Cleanup page's "Largest files" scan.</summary>
public sealed class LargeFileInfo
{
    public required string FullPath { get; init; }
    public required long SizeBytes { get; init; }
    public DateTime LastWriteTime { get; init; }

    public string FileName => System.IO.Path.GetFileName(FullPath);
    public string SizeText => SizeFormat.Format(SizeBytes);
    public string LastWriteText => LastWriteTime.ToString("yyyy-MM-dd");
}

/// <summary>
/// One file within a <see cref="DuplicateFileGroup"/>. IsSelected is
/// observable (CommunityToolkit's ObservableObject) so Select All/Select
/// None — which set it from the view model, not from the checkbox itself —
/// update the UI directly instead of needing the page to force-refresh the
/// whole list.
/// </summary>
public sealed partial class DuplicateFileEntry : ObservableObject
{
    public required string FullPath { get; init; }
    public DateTime LastWriteTime { get; init; }

    [ObservableProperty]
    private bool isSelected;

    public string FileName => System.IO.Path.GetFileName(FullPath);
    public string DirectoryText => System.IO.Path.GetDirectoryName(FullPath) ?? "";
    public string LastWriteText => LastWriteTime.ToString("yyyy-MM-dd");
}

/// <summary>
/// A set of files with identical content (same size, then confirmed with
/// a SHA-256 hash). The newest file is left unselected by default — the
/// rest are pre-checked as the likely copies to remove.
/// </summary>
public sealed class DuplicateFileGroup
{
    public required long SizeBytes { get; init; }
    public required List<DuplicateFileEntry> Files { get; init; }

    public string SizeText => SizeFormat.Format(SizeBytes);
    public string WastedSpaceText => SizeFormat.Format(SizeBytes * (Files.Count - 1));
    public string HeaderText => $"{Files.Count} copies · {SizeText} each · {WastedSpaceText} wasted";
}

public enum LogKind
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>
/// One line in the Activity Log. Mirrors the color-coded RichEdit log
/// from the original C++ app (log_util.cpp), now bound to an
/// ObservableCollection and rendered as animated ListView rows.
/// </summary>
public sealed class LogEntry
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public LogKind Kind { get; init; }
    public string Message { get; init; } = string.Empty;

    public string TimeText => Timestamp.ToString("HH:mm:ss");
}

/// <summary>
/// Automatic Cleanup Schedule configuration, backed by a Task Scheduler
/// entry created with schtasks — same approach as schedule_manager.cpp.
/// </summary>
public sealed class ScheduleSettings
{
    public bool IsEnabled { get; set; }
    public DayOfWeek DayOfWeek { get; set; } = DayOfWeek.Monday;
    public TimeSpan TimeOfDay { get; set; } = new(3, 0, 0);
    public bool RunHighestPrivileges { get; set; } = true;
}

/// <summary>Free/used space on one fixed drive, as reported by <see cref="Services.ResourceMonitorService"/>.</summary>
public sealed record DriveSnapshot(string Name, long TotalBytes, long UsedBytes)
{
    public double UsedPercent => TotalBytes <= 0 ? 0 : UsedBytes * 100.0 / TotalBytes;
    public string UsedText => $"{SizeFormat.Format(UsedBytes)} / {SizeFormat.Format(TotalBytes)}";
}

/// <summary>
/// A live "right now" reading of CPU load, RAM usage, and per-drive space —
/// polled on a timer by the Dashboard so the numbers actually move instead
/// of showing a one-time static total. CpuPercent is -1 when the perf
/// counter is unavailable (rare, some locked-down systems) so the UI can
/// show "—" instead of a misleading 0%.
/// </summary>
public sealed record ResourceSnapshot(double CpuPercent, double RamUsedGb, double RamTotalGb, List<DriveSnapshot> Drives)
{
    public double RamPercent => RamTotalGb <= 0 ? 0 : RamUsedGb * 100.0 / RamTotalGb;
    public string CpuText => CpuPercent < 0 ? "—" : $"{CpuPercent:0}%";
    public string RamText => RamTotalGb <= 0 ? "—" : $"{RamUsedGb:0.#} / {RamTotalGb:0.#} GB";
}

/// <summary>One System Restore checkpoint, as reported by <see cref="Services.RestorePointService"/>.</summary>
public sealed record RestorePointInfo(int SequenceNumber, string Description, DateTime CreationTime);

/// <summary>
/// How aggressively a <see cref="CleanupCategory"/> can be cleaned without asking
/// twice. <see cref="Safe"/> categories are regenerated by Windows/apps on demand
/// and are selected by default. <see cref="Caution"/> categories (e.g. crash memory
/// dumps) remove something a user might actually want to keep, so they start
/// unchecked and the Cleanup page calls that out visually.
/// </summary>
public enum CleanupRisk
{
    Safe,
    Caution
}

/// <summary>
/// One selectable row in the Cleanup page's category scan — e.g. "Temporary
/// Files", "Browser Caches", "Windows Update Cache". Produced (with a real,
/// measured size) by <see cref="Services.CleanupScanService.ScanAsync"/> before
/// anything is deleted, so the user picks what to clean with actual numbers in
/// front of them instead of an unlabeled "Clean now" button.
/// </summary>
public sealed partial class CleanupCategory : ObservableObject
{
    /// <summary>Stable id passed back to <see cref="Services.CleanupScanService.CleanSelectedAsync"/>.</summary>
    public required string Id { get; init; }
    public required string TitleKey { get; init; }
    public required string DescriptionKey { get; init; }
    public CleanupRisk Risk { get; init; } = CleanupRisk.Safe;

    [ObservableProperty]
    private bool isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    private long sizeBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    private int fileCount;

    // Resolved through LocalizationService at read time (same pattern as
    // StartupItem.SourceText) so the list reflects whatever language is
    // active when the scan results are bound to the page.
    public string Title => Services.LocalizationService.Instance.Get(TitleKey);
    public string Description => Services.LocalizationService.Instance.Get(DescriptionKey);
    public bool IsCaution => Risk == CleanupRisk.Caution;

    public string SizeText => FileCount == 0
        ? Services.LocalizationService.Instance.Get("Cleanup_CategoryEmpty")
        : string.Format(Services.LocalizationService.Instance.Get("Cleanup_CategorySizeFormat"), SizeFormat.Format(SizeBytes), FileCount);
}

/// <summary>Result of scanning every cleanup category, plus the grand total so the page can show it before anything is selected.</summary>
public sealed record CleanupScanResult(List<CleanupCategory> Categories)
{
    public long TotalBytes => Categories.Sum(c => c.SizeBytes);
}

/// <summary>Result of actually cleaning the categories the user picked.</summary>
public sealed record CleanupRunResult(long FreedBytes, int FilesDeleted, int Errors)
{
    public string FreedText => SizeFormat.Format(FreedBytes);
}

/// <summary>Which kind of storage the system drive sits on — decides whether Optimize runs a full defrag or a TRIM-only retrim.</summary>
public enum DiskMediaKind
{
    Unknown,
    Hdd,
    Ssd
}

/// <summary>Result of one Optimize pass's drive step, for the Performance page to report exactly what ran and why.</summary>
public sealed record DriveOptimizeResult(DiskMediaKind Kind, bool Succeeded, string DriveLetter);

/// <summary>
/// Result of <see cref="Services.PerformanceBoostService.FreeUpMemoryAsync"/> —
/// how many background processes had their working set trimmed and roughly how
/// much physical memory that returned to Windows' standby list.
/// </summary>
public sealed record MemoryTrimResult(int ProcessesTrimmed, long EstimatedFreedBytes)
{
    public string FreedText => SizeFormat.Format(Math.Max(0, EstimatedFreedBytes));
}

/// <summary>Windows Firewall state per network profile (Domain/Private/Public), from <see cref="Services.SecurityHealthService.GetFirewallStatusAsync"/>.</summary>
public sealed record FirewallStatus(bool AllEnabled, List<string> DisabledProfiles);

/// <summary>One physical disk's S.M.A.R.T.-derived health, from <see cref="Services.SecurityHealthService.GetDiskHealthStatusesAsync"/>.</summary>
public sealed record DiskHealthInfo(string Name, string HealthStatus);

public enum StartupSource
{
    RegistryRun,
    StartupFolder
}

/// <summary>
/// Authenticode signature outcome for a startup item's resolved executable,
/// from <see cref="Services.StartupManagerService.EnrichWithPublisherInfoAsync"/>.
/// </summary>
public enum StartupSignatureStatus
{
    /// <summary>Not checked yet, or the check itself failed (PowerShell unavailable) — never treated as risky on its own.</summary>
    Unknown,
    Valid,
    NotSigned,
    Invalid,
    FileMissing,
}

/// <summary>
/// A single startup entry, sourced from either HKCU\...\Run or the
/// user's Startup folder — the same two sources startup_manager.cpp reads.
/// </summary>
public sealed partial class StartupItem : ObservableObject
{
    public string Name { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;
    public StartupSource Source { get; init; }

    [ObservableProperty]
    private bool isEnabled = true;

    // Filled in best-effort by StartupManagerService.EnrichWithPublisherInfoAsync
    // after the list loads — these start at "don't know yet" so the list is
    // still useful immediately, before the (slightly slower) signature check finishes.
    // Observable (CommunityToolkit) so that enrichment, which mutates items
    // already on screen, updates the Publisher column and risk color in
    // place instead of needing the page to force-rebind the whole list.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PublisherText))]
    [NotifyPropertyChangedFor(nameof(IsRisky))]
    private bool fileExists = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PublisherText))]
    private string? publisher;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PublisherText))]
    [NotifyPropertyChangedFor(nameof(IsRisky))]
    private StartupSignatureStatus signatureStatus = StartupSignatureStatus.Unknown;

    public string SourceText => Services.LocalizationService.Instance.Get(
        Source == StartupSource.RegistryRun ? "Startup_SourceRegistry" : "Startup_SourceFolder");

    public string PublisherText
    {
        get
        {
            var loc = Services.LocalizationService.Instance;
            if (!FileExists) return loc.Get("Startup_FileMissing");
            return SignatureStatus switch
            {
                StartupSignatureStatus.Valid => string.IsNullOrWhiteSpace(Publisher) ? loc.Get("Startup_SignedUnknownPublisher") : Publisher,
                StartupSignatureStatus.NotSigned => loc.Get("Startup_Unsigned"),
                StartupSignatureStatus.Invalid => loc.Get("Startup_SignatureInvalid"),
                _ => loc.Get("Startup_Unknown"),
            };
        }
    }

    /// <summary>Drives the row's warning color — a missing file or a failed/absent signature, never just "unknown" (that's most often just "the check hasn't run yet").</summary>
    public bool IsRisky => !FileExists || SignatureStatus is StartupSignatureStatus.NotSigned or StartupSignatureStatus.Invalid;
}

/// <summary>
/// A progress update from a long-running maintenance task. Total &lt;= 0
/// means "can't be broken into steps" — the UI shows an indeterminate
/// (marquee) bar instead of a filled percentage.
/// </summary>
public sealed record TaskProgress(int Current, int Total, string Message)
{
    public bool IsIndeterminate => Total <= 0;

    public static TaskProgress Step(int current, int total, string message) => new(current, total, message);

    public static TaskProgress Busy(string message) => new(0, 0, message);
}
