using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolventUI.Models;
using SolventUI.Services;

namespace SolventUI.ViewModels;

/// <summary>
/// The Performance page's nine independent one-shot actions (optimize,
/// disk health, internet test, network repair, DNS, battery report, free
/// memory, driver updates). The old code-behind had grown its own
/// RunGuarded/RunGuardedWithResult pair to avoid copy-pasting a ninth
/// try/finally — that pair is now just <see cref="ViewModelBase.RunOperationAsync"/>,
/// shared with every other page instead of reinvented per page.
/// </summary>
public sealed partial class PerformanceViewModel : ViewModelBase
{
    public ObservableCollection<DriverUpdateInfo> DriverUpdates { get; } = new();

    [ObservableProperty] private string driveKindText = "";
    [ObservableProperty] private string driverUpdatesSummary = "";
    [ObservableProperty] private string memoryResultText = "";

    /// <summary>
    /// Best-effort, non-blocking: shows the user up front which
    /// optimization Optimize will actually run, instead of only revealing
    /// it in the log after the button is clicked. Deliberately not routed
    /// through RunOperationAsync — this is silent background information,
    /// not an operation with a progress bar.
    /// </summary>
    public async Task OnLoadedAsync()
    {
        try
        {
            var systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
            var kind = await PerformanceBoostService.DetectMediaKindAsync(systemDrive);
            var loc = LocalizationService.Instance;
            DriveKindText = kind switch
            {
                DiskMediaKind.Ssd => string.Format(loc.Get("Perf_DetectedSsdFormat"), systemDrive),
                DiskMediaKind.Hdd => string.Format(loc.Get("Perf_DetectedHddFormat"), systemDrive),
                _ => "",
            };
        }
        catch { /* purely informational — ignore on failure */ }
    }

    [RelayCommand]
    private async Task OptimizeAsync()
    {
        var loc = LocalizationService.Instance;
        await RunOperationAsync(
            TaskService.OptimizeSystemAsync,
            loc.Get("Perf_OptimizeBegin"), loc.Get("Common_Done"), loc.Get("Common_Cancelled"),
            confirm: (loc.Get("Perf_OptimizeConfirmTitle"), string.Format(loc.Get("Perf_OptimizeConfirmMessage"), Environment.NewLine)));
    }

    [RelayCommand]
    private Task CheckDiskHealthAsync() =>
        RunOperationAsync(
            (_, ct) => TaskService.CheckDiskHealthAsync(ct),
            LocalizationService.Instance.Get("Perf_DiskBegin"), LocalizationService.Instance.Get("Common_Done"), LocalizationService.Instance.Get("Common_Cancelled"));

    [RelayCommand]
    private Task TestInternetAsync() =>
        RunOperationAsync(
            (_, ct) => TaskService.TestInternetConnectionAsync(ct),
            LocalizationService.Instance.Get("Perf_InternetBegin"), LocalizationService.Instance.Get("Common_Done"), LocalizationService.Instance.Get("Common_Cancelled"));

    [RelayCommand]
    private async Task RepairNetworkAsync()
    {
        var loc = LocalizationService.Instance;
        await RunOperationAsync(
            TaskService.RepairNetworkAsync,
            loc.Get("Perf_NetworkBegin"), loc.Get("Common_Done"), loc.Get("Common_Cancelled"),
            confirm: (loc.Get("Perf_NetworkConfirmTitle"), string.Format(loc.Get("Perf_NetworkConfirmMessage"), Environment.NewLine)));
    }

    [RelayCommand]
    private async Task CheckDriverUpdatesAsync()
    {
        var loc = LocalizationService.Instance;
        DriverUpdates.Clear();
        DriverUpdatesSummary = "";

        List<DriverUpdateInfo>? result = null;
        await RunOperationAsync(
            async (progress, ct) => result = await DriverUpdateService.CheckForUpdatesAsync(progress, ct),
            loc.Get("Perf_DriverBegin"), loc.Get("Common_Done"), loc.Get("Common_Cancelled"));

        if (result is null) return; // cancelled
        foreach (var update in result) DriverUpdates.Add(update);
        DriverUpdatesSummary = result.Count == 0
            ? loc.Get("Perf_NoDriverUpdates")
            : string.Format(loc.Get("Perf_DriverUpdatesFormat"), result.Count);
    }

    [RelayCommand]
    private Task OpenWindowsUpdateAsync() => WindowsUpdateService.OpenUpdateSettingsAsync();

    [RelayCommand]
    private Task SetPublicDnsAsync() =>
        RunOperationAsync(NetworkDnsService.SetPublicDnsAsync,
            LocalizationService.Instance.Get("Perf_DnsPublicBegin"), LocalizationService.Instance.Get("Common_Done"), LocalizationService.Instance.Get("Common_Cancelled"));

    [RelayCommand]
    private Task ResetDnsAsync() =>
        RunOperationAsync(NetworkDnsService.ResetDnsToAutomaticAsync,
            LocalizationService.Instance.Get("Perf_DnsAutoBegin"), LocalizationService.Instance.Get("Common_Done"), LocalizationService.Instance.Get("Common_Cancelled"));

    [RelayCommand]
    private Task GenerateBatteryReportAsync() =>
        RunOperationAsync(BatteryReportService.GenerateAndOpenAsync,
            LocalizationService.Instance.Get("Perf_BatteryBegin"), LocalizationService.Instance.Get("Common_Done"), LocalizationService.Instance.Get("Common_Cancelled"));

    [RelayCommand]
    private async Task FreeMemoryAsync()
    {
        var loc = LocalizationService.Instance;
        MemoryResultText = "";

        MemoryTrimResult? result = null;
        await RunOperationAsync(
            async (progress, ct) => result = await PerformanceBoostService.FreeUpMemoryAsync(progress, ct),
            loc.Get("Perf_MemoryBegin"), loc.Get("Common_Done"), loc.Get("Common_Cancelled"));

        if (result is null) return; // cancelled
        MemoryResultText = string.Format(loc.Get("Perf_MemoryResultFormat"), result.ProcessesTrimmed, result.FreedText);
    }
}
