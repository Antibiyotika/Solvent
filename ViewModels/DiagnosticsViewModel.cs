using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolventUI.Models;
using SolventUI.Services;

namespace SolventUI.ViewModels;

/// <summary>
/// Health check score, findings list, and per-issue one-click fixes. Unlike
/// every other maintenance page, Diagnostics never showed a shared
/// progress bar (a health check is quick enough not to need one), so this
/// is a plain <see cref="ObservableObject"/> rather than a
/// <see cref="ViewModelBase"/> — nothing to gain from a pattern built
/// around a progress widget this page doesn't have.
/// </summary>
public sealed partial class DiagnosticsViewModel : ObservableObject
{
    private HealthCheckResult? _lastResult;

    public ObservableCollection<DiagnosticIssue> Issues { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExportEnabled))]
    private bool isBusy;

    [ObservableProperty] private string scoreText = "—";
    [ObservableProperty] private string scoreBrushKey = "SolventAccentBrush";
    [ObservableProperty] private string summaryText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExportEnabled))]
    private bool canExport;

    /// <summary>What the page binds Export's IsEnabled to: a result to export, and nothing else already running.</summary>
    public bool ExportEnabled => CanExport && !IsBusy;

    /// <summary>
    /// One fix (open-startup-manager) navigates to a whole other page
    /// instead of applying an in-place change. The view model can't do
    /// that navigation itself, so it asks the page to.
    /// </summary>
    public event Action? NavigateToStartupManagerRequested;

    public DiagnosticsViewModel()
    {
        SummaryText = LocalizationService.Instance.Get("Diag_DefaultSummary");

        // A health check triggered elsewhere (e.g. "Run All" on the Dashboard)
        // still produces a result — show it here too instead of leaving this
        // page's score/findings looking empty until the user clicks Run themselves.
        if (DiagnosticsService.LastResult is { } cached)
            ApplyResult(cached);

        DiagnosticsService.LastResultChanged += OnLastResultChangedElsewhere;
    }

    /// <summary>Call from the page's Unloaded — this view model subscribes to a static event, so it must detach or it leaks.</summary>
    public void Detach() => DiagnosticsService.LastResultChanged -= OnLastResultChangedElsewhere;

    // DiagnosticsService.LastResultChanged fires from whatever thread ran
    // the check (e.g. the Dashboard's "Run All" background task), so this
    // is the one place a view model needs System.Windows directly — there's
    // no clean, simpler alternative for marshalling a static service event
    // back to the UI thread.
    private void OnLastResultChangedElsewhere(HealthCheckResult result) =>
        System.Windows.Application.Current.Dispatcher.Invoke(() => ApplyResult(result));

    [RelayCommand]
    private async Task RunHealthCheckAsync()
    {
        IsBusy = true;
        try
        {
            var result = await DiagnosticsService.RunHealthCheckAsync();
            ApplyResult(result); // also reached via LastResultChanged, but apply immediately rather than waiting on the event round-trip
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Renders a health-check result into the score, summary, and findings list — used whether the check ran from this page's own button or from "Run All" elsewhere.</summary>
    private void ApplyResult(HealthCheckResult result)
    {
        var loc = LocalizationService.Instance;
        _lastResult = result;
        CanExport = true;

        ScoreText = result.Score.ToString();
        ScoreBrushKey = result.Score switch
        {
            >= 80 => "SolventSuccessBrush",
            >= 50 => "SolventWarningBrush",
            _ => "SolventErrorBrush",
        };

        var actionable = result.Issues.Count(i => i.Severity != IssueSeverity.Good);
        SummaryText = actionable == 0
            ? loc.Get("Diag_AllClean")
            : string.Format(loc.Get("Diag_ActionableFormat"), actionable);

        Issues.Clear();
        foreach (var issue in result.Issues)
            Issues.Add(issue);
    }

    [RelayCommand]
    private async Task FixAsync(DiagnosticIssue? issue)
    {
        if (issue?.FixActionId is null) return;

        IsBusy = true;
        try
        {
            switch (issue.FixActionId)
            {
                case "rescan-devices":
                    await DeviceHealthService.RescanHardwareAsync();
                    break;
                case "enable-defender":
                    await TaskService.EnableDefenderAsync();
                    break;
                case "clean-temp":
                    await TaskService.CleanTempAndCacheAsync();
                    break;
                case "open-driver-updates":
                    await WindowsUpdateService.OpenUpdateSettingsAsync();
                    break;
                case "repair-printer":
                    await PrinterRepairService.RepairSpoolerAsync();
                    break;
                case "free-memory":
                    await PerformanceBoostService.FreeUpMemoryAsync();
                    break;
                case "enable-firewall":
                    await SecurityHealthService.EnableFirewallAsync();
                    break;
                case "open-bitlocker":
                    await SecurityHealthService.OpenBitLockerSettingsAsync();
                    break;
                case "create-restore-point":
                    await TaskService.CreateRestorePointAsync();
                    break;
                case "open-startup-manager":
                    // Navigates away from this page entirely — skip the
                    // post-fix re-check below since there's nothing left
                    // here to refresh.
                    NavigateToStartupManagerRequested?.Invoke();
                    return;
            }
            // Re-run the check so the list (and score) reflect the fix that was just applied.
            await RunHealthCheckAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// The page owns the actual SaveFileDialog (a real OS dialog has no
    /// place in a testable view model); once the user picks a destination
    /// it calls this with the chosen path.
    /// </summary>
    public async Task ExportAsync(string filePath)
    {
        if (_lastResult is null) return;

        IsBusy = true;
        try
        {
            await DiagnosticsService.ExportReportAsync(_lastResult, filePath);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
