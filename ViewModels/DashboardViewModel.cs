using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolventUI.Services;

namespace SolventUI.ViewModels;

/// <summary>
/// Everything the Dashboard page shows: static system info, the live
/// CPU/RAM/drive gauges (polled on a timer), the rolled-up health score,
/// and the "Run All" operation. Pure state and logic — no
/// <c>System.Windows.Controls</c> types in sight, so this can be unit
/// tested without a UI thread. <see cref="SolventUI.Views.DashboardPage"/> only binds
/// to it and forwards its three operation events to the page's progress
/// bar (see <see cref="ViewModelBase"/>).
/// </summary>
public sealed partial class DashboardViewModel : ViewModelBase
{
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    [ObservableProperty] private string osText = "—";
    [ObservableProperty] private string buildText = "—";
    [ObservableProperty] private string adminText = "—";

    [ObservableProperty] private string cpuText = "—";
    [ObservableProperty] private double cpuPercent;
    [ObservableProperty] private string ramText = "—";
    [ObservableProperty] private double ramPercent;
    [ObservableProperty] private string driveText = "—";
    [ObservableProperty] private double drivePercent;

    [ObservableProperty] private string healthScoreText = "—";
    [ObservableProperty] private string healthDetailText = "—";
    [ObservableProperty] private string healthScoreLabel = "—";
    /// <summary>
    /// Theme brush key for the health label (e.g. "SolventSuccessBrush").
    /// A plain converter binding on Foreground would freeze at bind time;
    /// the page instead reacts to this property changing and calls
    /// SetResourceReference itself, so the color keeps following the live
    /// theme if the user switches it while this page is open — same
    /// behavior the old code-behind had, just triggered from here.
    /// </summary>
    [ObservableProperty] private string healthBrushKey = "SolventAccentBrush";

    public DashboardViewModel() => _liveTimer.Tick += (_, _) => RefreshLiveResources();

    public void OnLoaded()
    {
        RefreshSystemInfo();
        RefreshLiveResources();
        _liveTimer.Start();
    }

    public void OnUnloaded() => _liveTimer.Stop();

    private void RefreshSystemInfo()
    {
        var loc = LocalizationService.Instance;
        var info = SystemInfoService.GetSystemInfo();
        OsText = info.VersionLabel;
        BuildText = string.Format(loc.Get("Dashboard_BuildFormat"), info.BuildNumber, info.ProductName);
        AdminText = info.IsAdmin ? loc.Get("Dashboard_AdminYes") : loc.Get("Dashboard_AdminNo");
    }

    /// <summary>
    /// Polled every couple of seconds while this page is visible — real,
    /// moving CPU/RAM/disk numbers instead of the one-shot static totals
    /// Solvent used to show only once at startup.
    /// </summary>
    private void RefreshLiveResources()
    {
        var snapshot = ResourceMonitorService.GetSnapshot();

        CpuText = snapshot.CpuText;
        CpuPercent = Math.Max(0, snapshot.CpuPercent);

        RamText = snapshot.RamTotalGb <= 0 ? "—" : $"{snapshot.RamText} ({snapshot.RamPercent:0}%)";
        RamPercent = Math.Max(0, snapshot.RamPercent);

        var systemDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\', ':');
        var drive = snapshot.Drives.FirstOrDefault(d => d.Name.TrimEnd(':').Equals(systemDrive, StringComparison.OrdinalIgnoreCase));
        DriveText = drive is null
            ? Environment.GetEnvironmentVariable("SystemDrive") ?? "C:"
            : $"{drive.Name} {drive.UsedPercent:0}%";
        DrivePercent = Math.Max(0, drive?.UsedPercent ?? 0);

        UpdateHealthScore(CpuPercent, snapshot.RamTotalGb <= 0 ? 0 : snapshot.RamPercent, DrivePercent);
    }

    /// <summary>
    /// Rolls CPU load, RAM pressure and system-drive fullness into a single
    /// 0-100 score — a quick "is anything worth looking at?" glance instead
    /// of the user having to read and mentally weigh three separate cards.
    /// Weighted toward disk space and RAM, since a busy CPU for a couple of
    /// seconds is normal while the other two are the ones that actually
    /// tend to cause slowdowns and failures.
    /// </summary>
    private void UpdateHealthScore(double cpuPercent, double ramPercent, double drivePercent)
    {
        var loc = LocalizationService.Instance;

        double Penalty(double percent, double softLimit, double hardLimit, double weight)
        {
            if (percent <= softLimit) return 0;
            var over = Math.Min(1.0, (percent - softLimit) / Math.Max(1.0, hardLimit - softLimit));
            return over * weight;
        }

        var score = 100.0
            - Penalty(cpuPercent, 70, 100, 15)
            - Penalty(ramPercent, 75, 95, 30)
            - Penalty(drivePercent, 85, 98, 35);
        score = Math.Clamp(score, 0, 100);

        HealthScoreText = $"{score:0}";
        HealthDetailText = string.Format(
            loc.Get("Dashboard_HealthDetailFormat"), cpuPercent.ToString("0"), ramPercent.ToString("0"), drivePercent.ToString("0"));

        (HealthScoreLabel, HealthBrushKey) = score switch
        {
            >= 90 => (loc.Get("Dashboard_HealthExcellent"), "SolventSuccessBrush"),
            >= 70 => (loc.Get("Dashboard_HealthGood"), "SolventAccentBrush"),
            >= 50 => (loc.Get("Dashboard_HealthFair"), "SolventWarningBrush"),
            _ => (loc.Get("Dashboard_HealthPoor"), "SolventErrorBrush"),
        };
    }

    [RelayCommand]
    private async Task RunAllAsync()
    {
        var loc = LocalizationService.Instance;
        await RunOperationAsync(
            TaskService.RunAllAsync,
            loc.Get("Dashboard_RunAllBegin"),
            loc.Get("Dashboard_RunAllDone"),
            loc.Get("Dashboard_RunAllCancelled"),
            confirm: (loc.Get("Dashboard_ConfirmTitle"), string.Format(loc.Get("Dashboard_ConfirmMessage"), Environment.NewLine)));
        RefreshSystemInfo();
    }
}
