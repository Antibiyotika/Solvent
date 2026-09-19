using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using SolventUI.Models;
using SolventUI.Services;

namespace SolventUI.Views;

public partial class PerformancePage : Page
{
    private CancellationTokenSource? _cts;
    private readonly ObservableCollection<DriverUpdateInfo> _driverUpdates = new();

    public PerformancePage()
    {
        InitializeComponent();
        DriverUpdatesList.ItemsSource = _driverUpdates;
        Loaded += PerformancePage_OnLoaded;
    }

    private async void PerformancePage_OnLoaded(object sender, RoutedEventArgs e)
    {
        // Best-effort, non-blocking: shows the user up front which
        // optimization Optimize will actually run, instead of only
        // revealing it in the log after the button is clicked.
        try
        {
            var systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
            var kind = await PerformanceBoostService.DetectMediaKindAsync(systemDrive);
            var loc = LocalizationService.Instance;
            DriveKindText.Text = kind switch
            {
                DiskMediaKind.Ssd => string.Format(loc.Get("Perf_DetectedSsdFormat"), systemDrive),
                DiskMediaKind.Hdd => string.Format(loc.Get("Perf_DetectedHddFormat"), systemDrive),
                _ => "",
            };
        }
        catch { /* purely informational — ignore on failure */ }
    }

    private async void Optimize_OnClick(object sender, RoutedEventArgs e)
    {
        var loc = LocalizationService.Instance;
        if (!ConfirmationService.Ask(
                loc.Get("Perf_OptimizeConfirmTitle"),
                string.Format(loc.Get("Perf_OptimizeConfirmMessage"), Environment.NewLine)))
            return;

        await RunGuarded(OptimizeButton, loc.Get("Perf_OptimizeBegin"), TaskService.OptimizeSystemAsync);
    }

    private async void DiskHealth_OnClick(object sender, RoutedEventArgs e) =>
        await RunGuarded(DiskButton, LocalizationService.Instance.Get("Perf_DiskBegin"), (_, ct) => TaskService.CheckDiskHealthAsync(ct));

    private async void InternetTest_OnClick(object sender, RoutedEventArgs e) =>
        await RunGuarded(InternetButton, LocalizationService.Instance.Get("Perf_InternetBegin"), (_, ct) => TaskService.TestInternetConnectionAsync(ct));

    private async void NetworkRepair_OnClick(object sender, RoutedEventArgs e)
    {
        var loc = LocalizationService.Instance;
        if (!ConfirmationService.Ask(
                loc.Get("Perf_NetworkConfirmTitle"),
                string.Format(loc.Get("Perf_NetworkConfirmMessage"), Environment.NewLine)))
            return;

        await RunGuarded(NetworkRepairButton, loc.Get("Perf_NetworkBegin"), TaskService.RepairNetworkAsync);
    }

    private async void DriverUpdates_OnClick(object sender, RoutedEventArgs e)
    {
        var loc = LocalizationService.Instance;
        _driverUpdates.Clear();
        DriverUpdatesSummary.Text = "";

        var result = await RunGuardedWithResult(DriverUpdatesButton, loc.Get("Perf_DriverBegin"), DriverUpdateService.CheckForUpdatesAsync);
        if (result is null) return; // cancelled

        foreach (var u in result) _driverUpdates.Add(u);
        DriverUpdatesSummary.Text = result.Count == 0
            ? loc.Get("Perf_NoDriverUpdates")
            : string.Format(loc.Get("Perf_DriverUpdatesFormat"), result.Count);
    }

    private async void OpenWindowsUpdate_OnClick(object sender, RoutedEventArgs e) =>
        await WindowsUpdateService.OpenUpdateSettingsAsync();

    private async void SetPublicDns_OnClick(object sender, RoutedEventArgs e) =>
        await RunGuarded(SetPublicDnsButton, LocalizationService.Instance.Get("Perf_DnsPublicBegin"), NetworkDnsService.SetPublicDnsAsync);

    private async void ResetDns_OnClick(object sender, RoutedEventArgs e) =>
        await RunGuarded(ResetDnsButton, LocalizationService.Instance.Get("Perf_DnsAutoBegin"), NetworkDnsService.ResetDnsToAutomaticAsync);

    private async void BatteryReport_OnClick(object sender, RoutedEventArgs e) =>
        await RunGuarded(BatteryReportButton, LocalizationService.Instance.Get("Perf_BatteryBegin"), BatteryReportService.GenerateAndOpenAsync);

    private async void FreeMemory_OnClick(object sender, RoutedEventArgs e)
    {
        var loc = LocalizationService.Instance;
        MemoryResultText.Text = "";
        var result = await RunGuardedWithResult(FreeMemoryButton, loc.Get("Perf_MemoryBegin"), PerformanceBoostService.FreeUpMemoryAsync);
        if (result is null) return; // cancelled
        MemoryResultText.Text = string.Format(loc.Get("Perf_MemoryResultFormat"), result.ProcessesTrimmed, result.FreedText);
    }

    /// <summary>Runs a void task with the shared progress bar + working Cancel button.</summary>
    private async Task RunGuarded(Control button, string startMessage, Func<IProgress<TaskProgress>?, CancellationToken, Task> action)
    {
        var loc = LocalizationService.Instance;
        button.IsEnabled = false;
        _cts = new CancellationTokenSource();
        ProgressPanel.Begin(startMessage);
        var reporter = new Progress<TaskProgress>(ProgressPanel.Report);

        try
        {
            await action(reporter, _cts.Token);
            ProgressPanel.Finish(loc.Get("Common_Done"));
        }
        catch (OperationCanceledException)
        {
            ProgressPanel.Finish(loc.Get("Common_Cancelled"));
        }
        finally
        {
            button.IsEnabled = true;
            _cts.Dispose();
            _cts = null;
            await Task.Delay(1200);
            ProgressPanel.Hide();
        }
    }

    /// <summary>Same as <see cref="RunGuarded"/> but for a task that returns a result; null means it was cancelled.</summary>
    private async Task<T?> RunGuardedWithResult<T>(
        Control button, string startMessage, Func<IProgress<TaskProgress>?, CancellationToken, Task<T>> action) where T : class
    {
        var loc = LocalizationService.Instance;
        button.IsEnabled = false;
        _cts = new CancellationTokenSource();
        ProgressPanel.Begin(startMessage);
        var reporter = new Progress<TaskProgress>(ProgressPanel.Report);

        try
        {
            var result = await action(reporter, _cts.Token);
            ProgressPanel.Finish(loc.Get("Common_Done"));
            return result;
        }
        catch (OperationCanceledException)
        {
            ProgressPanel.Finish(loc.Get("Common_Cancelled"));
            return null;
        }
        finally
        {
            button.IsEnabled = true;
            _cts.Dispose();
            _cts = null;
            await Task.Delay(1200);
            ProgressPanel.Hide();
        }
    }

    private void Progress_OnCancelRequested(object? sender, EventArgs e) => _cts?.Cancel();
}
