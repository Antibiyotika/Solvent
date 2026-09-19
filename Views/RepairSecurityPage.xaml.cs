using System.Windows;
using System.Windows.Controls;
using SolventUI.Models;
using SolventUI.Services;

namespace SolventUI.Views;

public partial class RepairSecurityPage : Page
{
    private CancellationTokenSource? _cts;

    public RepairSecurityPage() => InitializeComponent();

    private async void Repair_OnClick(object sender, RoutedEventArgs e) =>
        await RunGuarded(RepairButton, LocalizationService.Instance.Get("RS_RepairBegin"), TaskService.RepairSystemErrorsAsync);

    private async void Defender_OnClick(object sender, RoutedEventArgs e) =>
        await RunGuarded(DefenderButton, LocalizationService.Instance.Get("RS_DefenderBegin"), TaskService.EnableDefenderAsync);

    private async void RestorePoint_OnClick(object sender, RoutedEventArgs e) =>
        await RunGuarded(RestoreButton, LocalizationService.Instance.Get("RS_RestoreBegin"), TaskService.CreateRestorePointAsync);

    private async void WindowsUpdate_OnClick(object sender, RoutedEventArgs e)
    {
        var loc = LocalizationService.Instance;
        if (!ConfirmationService.Ask(
                loc.Get("RS_UpdateButton"),
                string.Format(loc.Get("RS_UpdateConfirmMessage"), Environment.NewLine)))
            return;

        await RunGuarded(WindowsUpdateButton, loc.Get("RS_UpdateBegin"), WindowsUpdateService.ResetComponentsAsync);
    }

    private async void MalwareScan_OnClick(object sender, RoutedEventArgs e) =>
        await RunGuarded(MalwareScanButton, LocalizationService.Instance.Get("RS_MalwareBegin"), MalwareScanService.RunQuickScanAsync);

    private async void MemoryTest_OnClick(object sender, RoutedEventArgs e) =>
        await RunGuarded(MemoryTestButton, LocalizationService.Instance.Get("RS_MemoryBegin"), MemoryDiagnosticService.ScheduleTestAsync);

    private async void PrinterRepair_OnClick(object sender, RoutedEventArgs e) =>
        await RunGuarded(PrinterRepairButton, LocalizationService.Instance.Get("RS_PrinterBegin"), PrinterRepairService.RepairSpoolerAsync);

    private async void DiskCheck_OnClick(object sender, RoutedEventArgs e)
    {
        var loc = LocalizationService.Instance;
        if (!ConfirmationService.Ask(
                loc.Get("RS_DiskCheckButton"),
                string.Format(loc.Get("RS_DiskCheckConfirmMessage"), Environment.NewLine)))
            return;

        await RunGuarded(DiskCheckButton, loc.Get("RS_DiskCheckBegin"), DiskCheckService.ScheduleCheckAsync);
    }

    private void RestoreBrowse_OnClick(object sender, RoutedEventArgs e) => RestorePointService.OpenRestoreWizard();

    /// <summary>
    /// Runs a task with the shared progress bar visible and a working
    /// Cancel button: creates a fresh CancellationTokenSource, feeds a
    /// Progress&lt;TaskProgress&gt; into the service call (Progress&lt;T&gt;
    /// marshals back to this UI thread automatically), and leaves the
    /// final status on screen for a moment before hiding the bar.
    /// </summary>
    private async Task RunGuarded(Control button, string startMessage, Func<IProgress<TaskProgress>?, CancellationToken, Task> action)
    {
        var loc = LocalizationService.Instance;
        button.IsEnabled = false;
        _cts = new CancellationTokenSource();
        ProgressPanel.Begin(startMessage);
        var reporter = new System.Progress<TaskProgress>(ProgressPanel.Report);

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

    private void Progress_OnCancelRequested(object? sender, EventArgs e) => _cts?.Cancel();
}
