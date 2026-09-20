using CommunityToolkit.Mvvm.Input;
using SolventUI.Services;

namespace SolventUI.ViewModels;

/// <summary>Repair &amp; Security page: eight independent one-shot maintenance actions.</summary>
public sealed partial class RepairSecurityViewModel : ViewModelBase
{
    [RelayCommand]
    private Task RepairAsync() =>
        RunOperationAsync(TaskService.RepairSystemErrorsAsync,
            LocalizationService.Instance.Get("RS_RepairBegin"), LocalizationService.Instance.Get("Common_Done"), LocalizationService.Instance.Get("Common_Cancelled"));

    [RelayCommand]
    private Task EnableDefenderAsync() =>
        RunOperationAsync(TaskService.EnableDefenderAsync,
            LocalizationService.Instance.Get("RS_DefenderBegin"), LocalizationService.Instance.Get("Common_Done"), LocalizationService.Instance.Get("Common_Cancelled"));

    [RelayCommand]
    private Task CreateRestorePointAsync() =>
        RunOperationAsync(TaskService.CreateRestorePointAsync,
            LocalizationService.Instance.Get("RS_RestoreBegin"), LocalizationService.Instance.Get("Common_Done"), LocalizationService.Instance.Get("Common_Cancelled"));

    [RelayCommand]
    private async Task ResetWindowsUpdateAsync()
    {
        var loc = LocalizationService.Instance;
        await RunOperationAsync(
            WindowsUpdateService.ResetComponentsAsync,
            loc.Get("RS_UpdateBegin"), loc.Get("Common_Done"), loc.Get("Common_Cancelled"),
            confirm: (loc.Get("RS_UpdateButton"), string.Format(loc.Get("RS_UpdateConfirmMessage"), Environment.NewLine)));
    }

    [RelayCommand]
    private Task MalwareScanAsync() =>
        RunOperationAsync(MalwareScanService.RunQuickScanAsync,
            LocalizationService.Instance.Get("RS_MalwareBegin"), LocalizationService.Instance.Get("Common_Done"), LocalizationService.Instance.Get("Common_Cancelled"));

    [RelayCommand]
    private Task ScheduleMemoryTestAsync() =>
        RunOperationAsync(MemoryDiagnosticService.ScheduleTestAsync,
            LocalizationService.Instance.Get("RS_MemoryBegin"), LocalizationService.Instance.Get("Common_Done"), LocalizationService.Instance.Get("Common_Cancelled"));

    [RelayCommand]
    private Task RepairPrinterAsync() =>
        RunOperationAsync(PrinterRepairService.RepairSpoolerAsync,
            LocalizationService.Instance.Get("RS_PrinterBegin"), LocalizationService.Instance.Get("Common_Done"), LocalizationService.Instance.Get("Common_Cancelled"));

    [RelayCommand]
    private async Task ScheduleDiskCheckAsync()
    {
        var loc = LocalizationService.Instance;
        await RunOperationAsync(
            DiskCheckService.ScheduleCheckAsync,
            loc.Get("RS_DiskCheckBegin"), loc.Get("Common_Done"), loc.Get("Common_Cancelled"),
            confirm: (loc.Get("RS_DiskCheckButton"), string.Format(loc.Get("RS_DiskCheckConfirmMessage"), Environment.NewLine)));
    }

    [RelayCommand]
    private void OpenRestoreWizard() => RestorePointService.OpenRestoreWizard();
}
