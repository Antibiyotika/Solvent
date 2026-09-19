using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SolventUI.Models;
using SolventUI.Services;

namespace SolventUI.Views;

public partial class DiagnosticsPage : Page
{
    private readonly ObservableCollection<DiagnosticIssue> _issues = new();
    private HealthCheckResult? _lastResult;

    public DiagnosticsPage()
    {
        InitializeComponent();
        IssuesList.ItemsSource = _issues;

        // A health check triggered elsewhere (e.g. "Run All" on the Dashboard)
        // still produces a result — show it here too instead of leaving this
        // page's score/findings looking empty until the user clicks Run themselves.
        if (DiagnosticsService.LastResult is { } cached)
            ApplyResult(cached);

        DiagnosticsService.LastResultChanged += OnLastResultChangedElsewhere;
        Unloaded += (_, _) => DiagnosticsService.LastResultChanged -= OnLastResultChangedElsewhere;
    }

    private void OnLastResultChangedElsewhere(HealthCheckResult result) =>
        Dispatcher.Invoke(() => ApplyResult(result));

    private async void RunHealthCheck_OnClick(object sender, RoutedEventArgs e) => await RunHealthCheckAsync();

    private async Task RunHealthCheckAsync()
    {
        RunButton.IsEnabled = false;
        try
        {
            var result = await DiagnosticsService.RunHealthCheckAsync();
            ApplyResult(result); // also reached via LastResultChanged, but apply immediately rather than waiting on the event round-trip
        }
        finally
        {
            RunButton.IsEnabled = true;
        }
    }

    /// <summary>Renders a health-check result into the score, summary, and findings list — used whether the check ran from this page's own button or from "Run All" elsewhere.</summary>
    private void ApplyResult(HealthCheckResult result)
    {
        var loc = LocalizationService.Instance;
        _lastResult = result;
        ExportButton.IsEnabled = true;

        ScoreText.Text = result.Score.ToString();
        ScoreText.Foreground = (System.Windows.Media.Brush)TryFindResource(
            result.Score >= 80 ? "SolventSuccessBrush" :
            result.Score >= 50 ? "SolventWarningBrush" : "SolventErrorBrush");

        var actionable = result.Issues.Count(i => i.Severity != IssueSeverity.Good);
        SummaryText.Text = actionable == 0
            ? loc.Get("Diag_AllClean")
            : string.Format(loc.Get("Diag_ActionableFormat"), actionable);

        _issues.Clear();
        foreach (var issue in result.Issues)
            _issues.Add(issue);
    }

    private async void Fix_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: DiagnosticIssue issue } button) return;
        if (issue.FixActionId is null) return;

        button.IsEnabled = false;
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
                    NavigationService?.Navigate(new StartupManagerPage());
                    return;
            }
            // Re-run the check so the list (and score) reflect the fix that was just applied.
            await RunHealthCheckAsync();
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async void Export_OnClick(object sender, RoutedEventArgs e)
    {
        if (_lastResult is null) return;

        var loc = LocalizationService.Instance;
        var dialog = new SaveFileDialog
        {
            Title = loc.Get("Diag_ExportDialogTitle"),
            Filter = $"{loc.Get("Diag_FilterHtml")}|*.html|{loc.Get("Diag_FilterText")}|*.txt",
            FileName = $"Solvent-HealthCheck-{DateTime.Now:yyyy-MM-dd}.html",
        };

        if (dialog.ShowDialog() != true) return;

        ExportButton.IsEnabled = false;
        try
        {
            await DiagnosticsService.ExportReportAsync(_lastResult, dialog.FileName);
        }
        finally
        {
            ExportButton.IsEnabled = true;
        }
    }
}
