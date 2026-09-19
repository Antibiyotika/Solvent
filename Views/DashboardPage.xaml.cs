using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SolventUI.Models;
using SolventUI.Services;

namespace SolventUI.Views;

public partial class DashboardPage : Page
{
    private CancellationTokenSource? _cts;
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public DashboardPage()
    {
        InitializeComponent();
        _liveTimer.Tick += (_, _) => RefreshLiveResources();
        Loaded += (_, _) =>
        {
            RefreshSystemInfo();
            RefreshLiveResources();
            _liveTimer.Start();
        };
        Unloaded += (_, _) => _liveTimer.Stop();
    }

    private void RefreshSystemInfo()
    {
        var loc = LocalizationService.Instance;
        var info = SystemInfoService.GetSystemInfo();
        OsText.Text = info.VersionLabel;
        BuildText.Text = string.Format(loc.Get("Dashboard_BuildFormat"), info.BuildNumber, info.ProductName);
        AdminText.Text = info.IsAdmin ? loc.Get("Dashboard_AdminYes") : loc.Get("Dashboard_AdminNo");
    }

    /// <summary>
    /// Polled every couple of seconds while this page is visible — real,
    /// moving CPU/RAM/disk numbers instead of the one-shot static totals
    /// Solvent used to show only once at startup.
    /// </summary>
    private void RefreshLiveResources()
    {
        var snapshot = ResourceMonitorService.GetSnapshot();

        CpuText.Text = snapshot.CpuText;
        CpuBar.Value = Math.Max(0, snapshot.CpuPercent);

        RamText.Text = snapshot.RamTotalGb <= 0 ? "—" : $"{snapshot.RamText} ({snapshot.RamPercent:0}%)";

        var systemDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\', ':');
        var drive = snapshot.Drives.FirstOrDefault(d => d.Name.TrimEnd(':').Equals(systemDrive, StringComparison.OrdinalIgnoreCase));
        DriveText.Text = drive is null
            ? Environment.GetEnvironmentVariable("SystemDrive") ?? "C:"
            : $"{drive.Name} {drive.UsedPercent:0}%";
    }

    private async void RunAll_OnClick(object sender, RoutedEventArgs e)
    {
        var loc = LocalizationService.Instance;
        if (!ConfirmationService.Ask(
                loc.Get("Dashboard_ConfirmTitle"),
                string.Format(loc.Get("Dashboard_ConfirmMessage"), Environment.NewLine)))
            return;

        RunAllButton.IsEnabled = false;
        _cts = new CancellationTokenSource();
        ProgressPanel.Begin(loc.Get("Dashboard_RunAllBegin"));
        var reporter = new Progress<TaskProgress>(ProgressPanel.Report);

        try
        {
            await TaskService.RunAllAsync(reporter, _cts.Token);
            ProgressPanel.Finish(loc.Get("Dashboard_RunAllDone"));
        }
        catch (OperationCanceledException)
        {
            ProgressPanel.Finish(loc.Get("Dashboard_RunAllCancelled"));
        }
        finally
        {
            RunAllButton.IsEnabled = true;
            _cts.Dispose();
            _cts = null;
            RefreshSystemInfo();
            await Task.Delay(1500);
            ProgressPanel.Hide();
        }
    }

    private void Progress_OnCancelRequested(object? sender, EventArgs e) => _cts?.Cancel();
}
