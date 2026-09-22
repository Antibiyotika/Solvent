using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolventUI.Models;
using SolventUI.Services;

namespace SolventUI.ViewModels;

/// <summary>
/// Everything the Cleanup page does: the scan-then-select deep clean, the
/// large-files finder, and the duplicate-files finder. All three used to
/// share one <c>_cts</c> field and one <c>OperationProgressBar</c> in
/// <see cref="SolventUI.Views.CleanupPage"/>'s code-behind with a try/finally copied
/// three times over; here they all go through the one shared
/// <see cref="ViewModelBase.RunOperationAsync"/> instead, and the page binds
/// straight to the collections and text below.
/// </summary>
public sealed partial class CleanupViewModel : ViewModelBase
{
    private readonly CleanupHistoryService _history;
    private long _largeFilesThreshold = 50 * 1024 * 1024;
    private string? _largeFilesRoot;

    public CleanupViewModel(CleanupHistoryService history) => _history = history;

    public ObservableCollection<CleanupCategory> Categories { get; } = new();
    public ObservableCollection<LargeFileInfo> LargeFiles { get; } = new();
    public ObservableCollection<DuplicateFileGroup> DuplicateGroups { get; } = new();
    public ObservableCollection<DriveOption> LargeFilesDrives { get; } = new();

    [ObservableProperty] private bool hasScanResults;
    [ObservableProperty] private string scanTotalText = "";
    [ObservableProperty] private string selectionSummaryText = "";

    [ObservableProperty] private bool showLargeFiles;
    [ObservableProperty] private string largeFilesSelectionText = "";

    [ObservableProperty] private bool showDuplicates;
    [ObservableProperty] private string duplicatesSummaryText = "";
    [ObservableProperty] private string duplicatesSelectionText = "";

    // ---------- Deep Clean (scan → select → clean) ----------

    [RelayCommand]
    private async Task ScanAsync()
    {
        var loc = LocalizationService.Instance;
        CleanupScanResult? result = null;

        await RunOperationAsync(
            async (_, ct) => result = await CleanupScanService.ScanAsync(ct),
            loc.Get("Cleanup_ScanBegin"),
            loc.Get("Common_Done"),
            loc.Get("Common_Cancelled"));

        if (result is null) return; // cancelled before the scan finished
        Categories.Clear();
        foreach (var category in result.Categories) Categories.Add(category);
        HasScanResults = true;
        UpdateCategorySelectionText();
    }

    [RelayCommand]
    private void SelectAllCategories() => SetAllCategorySelection(true);

    [RelayCommand]
    private void SelectNoneCategories() => SetAllCategorySelection(false);

    private void SetAllCategorySelection(bool selected)
    {
        foreach (var category in Categories) category.IsSelected = selected;
        UpdateCategorySelectionText();
    }

    private void UpdateCategorySelectionText()
    {
        var loc = LocalizationService.Instance;
        var total = Categories.Sum(c => c.SizeBytes);
        ScanTotalText = string.Format(loc.Get("Cleanup_ScanTotalFormat"), SizeFormat.Format(total));

        var selected = Categories.Where(c => c.IsSelected).ToList();
        var selectedBytes = selected.Sum(c => c.SizeBytes);
        SelectionSummaryText = selected.Count == 0
            ? loc.Get("Cleanup_NothingSelected")
            : string.Format(loc.Get("Cleanup_CategorySelectedFormat"), selected.Count, SizeFormat.Format(selectedBytes));
    }

    [RelayCommand]
    private async Task CleanSelectedAsync()
    {
        var loc = LocalizationService.Instance;
        var selected = Categories.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0) return;

        var hasCaution = selected.Any(c => c.Risk == CleanupRisk.Caution);
        var confirmMessage = string.Format(
            loc.Get(hasCaution ? "Cleanup_CleanSelectedCautionConfirmMessage" : "Cleanup_CleanSelectedConfirmMessage"),
            SizeFormat.Format(selected.Sum(c => c.SizeBytes)), Environment.NewLine);

        var ids = selected.Select(c => c.Id).ToList();
        CleanupRunResult? result = null;
        CleanupScanResult? rescan = null;

        await RunOperationAsync(
            async (progress, ct) =>
            {
                result = await CleanupScanService.CleanSelectedAsync(ids, progress, ct);
                // Re-scan so the list reflects what's actually left (0 bytes for
                // whatever just got cleaned) instead of showing stale numbers.
                rescan = await CleanupScanService.ScanAsync(ct);
            },
            loc.Get("Cleanup_CleanBegin"),
            loc.Get("Common_Done"),
            loc.Get("Common_Cancelled"),
            confirm: (loc.Get("Cleanup_CleanConfirmTitle"), confirmMessage),
            doneMessageFactory: () => string.Format(loc.Get("Cleanup_FreedFormat"), result!.FreedText));

        if (result is not null)
            _history.Record(result, CleanupHistoryService.SourceManual);

        if (rescan is not null)
        {
            Categories.Clear();
            foreach (var category in rescan.Categories) Categories.Add(category);
        }
        UpdateCategorySelectionText();
    }

    // ---------- Large Files ----------

    [RelayCommand]
    private async Task ShowLargeFilesAsync()
    {
        ShowLargeFiles = true;
        if (LargeFilesDrives.Count == 0)
            foreach (var drive in TaskService.GetLargeFilesDriveOptions()) LargeFilesDrives.Add(drive);
        await RunLargeFilesScanAsync();
    }

    /// <summary>
    /// Called from the page's ComboBox selection-changed handler (parsing
    /// the selected item's Tag is a view concern; deciding what happens
    /// next isn't). Only re-scans if the panel is already open and showing
    /// a previous result — changing the threshold before ever scanning
    /// just sets the default for the first scan.
    /// </summary>
    public void SetLargeFilesThreshold(long bytes)
    {
        _largeFilesThreshold = bytes;
        if (ShowLargeFiles && LargeFiles.Count > 0)
            _ = RunLargeFilesScanAsync();
    }

    /// <summary>Called from the page's drive-picker ComboBox. Same re-scan-only-if-already-shown rule as the threshold picker — and a no-op if the root didn't actually change, so the initial auto-select (page opens, ComboBox settles on its default item) doesn't trigger a redundant second scan right after ShowLargeFilesAsync's own.</summary>
    public void SetLargeFilesDrive(string? root)
    {
        if (root == _largeFilesRoot) return;
        _largeFilesRoot = root;
        if (ShowLargeFiles)
            _ = RunLargeFilesScanAsync();
    }

    private async Task RunLargeFilesScanAsync()
    {
        var loc = LocalizationService.Instance;
        LargeFiles.Clear();
        List<LargeFileInfo>? results = null;

        await RunOperationAsync(
            async (_, ct) => results = await TaskService.FindLargeFilesAsync(minSizeBytes: _largeFilesThreshold, root: _largeFilesRoot, ct: ct),
            loc.Get("Cleanup_LargeFilesBegin"),
            loc.Get("Common_Done"),
            loc.Get("Common_Cancelled"));

        if (results is null) return;
        foreach (var file in results) LargeFiles.Add(file);
        UpdateLargeFilesSelectionText();
    }

    [RelayCommand]
    private void OpenFolder(LargeFileInfo? file)
    {
        if (file is null) return;
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{file.FullPath}\"");
        }
        catch { /* best-effort — nothing to recover from here */ }
    }

    [RelayCommand]
    private void SelectAllLargeFiles() => SetAllLargeFilesSelection(true);

    [RelayCommand]
    private void SelectNoneLargeFiles() => SetAllLargeFilesSelection(false);

    private void SetAllLargeFilesSelection(bool selected)
    {
        foreach (var file in LargeFiles) file.IsSelected = selected;
        UpdateLargeFilesSelectionText();
    }

    /// <summary>Bound to each row's checkbox (Checked/Unchecked) so the selection summary stays live while the panel is open.</summary>
    public void OnLargeFileSelectionChanged() => UpdateLargeFilesSelectionText();

    private void UpdateLargeFilesSelectionText()
    {
        var loc = LocalizationService.Instance;
        var selected = LargeFiles.Where(f => f.IsSelected).ToList();
        LargeFilesSelectionText = selected.Count == 0
            ? loc.Get("Cleanup_NothingSelected")
            : string.Format(loc.Get("Cleanup_SelectedFormat"), selected.Count, SizeFormat.Format(selected.Sum(f => f.SizeBytes)));
    }

    [RelayCommand]
    private async Task DeleteLargeFilesAsync()
    {
        var loc = LocalizationService.Instance;
        var selected = LargeFiles.Where(f => f.IsSelected).ToList();
        if (selected.Count == 0) return;

        await RunOperationAsync(
            async (_, ct) =>
            {
                await TaskService.DeleteFilesToRecycleBinAsync(selected.Select(f => f.FullPath), ct);
                foreach (var file in selected) LargeFiles.Remove(file);
                UpdateLargeFilesSelectionText();
            },
            loc.Get("Cleanup_DeletingBegin"),
            loc.Get("Common_Done"),
            loc.Get("Common_Cancelled"),
            confirm: (loc.Get("Cleanup_DeleteConfirmTitle"),
                string.Format(loc.Get("Cleanup_DeleteConfirmMessageFormat"), selected.Count, Environment.NewLine)));
    }

    // ---------- Duplicate Files ----------

    [RelayCommand]
    private async Task ShowDuplicatesAsync()
    {
        var loc = LocalizationService.Instance;
        ShowDuplicates = true;
        DuplicateGroups.Clear();
        DuplicatesSummaryText = loc.Get("Cleanup_DuplicatesDefaultSummary");
        UpdateDuplicatesSelectionText();

        List<DuplicateFileGroup>? groups = null;
        await RunOperationAsync(
            async (progress, ct) => groups = await TaskService.FindDuplicateFilesAsync(progress, ct),
            loc.Get("Cleanup_DuplicatesBegin"),
            loc.Get("Common_Done"),
            loc.Get("Common_Cancelled"));

        if (groups is null) return;
        foreach (var group in groups) DuplicateGroups.Add(group);

        var wastedBytes = groups.Sum(g => g.SizeBytes * (g.Files.Count - 1));
        DuplicatesSummaryText = groups.Count == 0
            ? loc.Get("Cleanup_NoDuplicates")
            : string.Format(loc.Get("Cleanup_DuplicatesFoundFormat"), groups.Count, SizeFormat.Format(wastedBytes));
        UpdateDuplicatesSelectionText();
    }

    [RelayCommand]
    private void SelectAllDuplicates() => SetAllDuplicateSelection(true);

    [RelayCommand]
    private void SelectNoneDuplicates() => SetAllDuplicateSelection(false);

    private void SetAllDuplicateSelection(bool selected)
    {
        foreach (var group in DuplicateGroups)
            foreach (var file in group.Files)
                file.IsSelected = selected;
        UpdateDuplicatesSelectionText();
    }

    private void UpdateDuplicatesSelectionText()
    {
        var loc = LocalizationService.Instance;
        var selected = DuplicateGroups
            .SelectMany(g => g.Files.Select(f => (f, g.SizeBytes)))
            .Where(x => x.f.IsSelected)
            .ToList();
        var bytes = selected.Sum(x => x.SizeBytes);
        DuplicatesSelectionText = selected.Count == 0
            ? loc.Get("Cleanup_NothingSelected")
            : string.Format(loc.Get("Cleanup_SelectedFormat"), selected.Count, SizeFormat.Format(bytes));
    }

    [RelayCommand]
    private async Task DeleteDuplicatesAsync()
    {
        var loc = LocalizationService.Instance;
        var selectedPaths = DuplicateGroups.SelectMany(g => g.Files).Where(f => f.IsSelected).Select(f => f.FullPath).ToList();
        if (selectedPaths.Count == 0) return;

        await RunOperationAsync(
            async (_, ct) =>
            {
                await TaskService.DeleteFilesToRecycleBinAsync(selectedPaths, ct);

                // Rebuild each affected group in place: assigning a new
                // DuplicateFileGroup at the same index raises a Replace
                // notification, so the header text and file list redraw
                // with the current counts — no manual ItemsSource reset.
                for (var i = DuplicateGroups.Count - 1; i >= 0; i--)
                {
                    var group = DuplicateGroups[i];
                    var remaining = group.Files.Where(f => !f.IsSelected).ToList();
                    if (remaining.Count <= 1)
                        DuplicateGroups.RemoveAt(i);
                    else
                        DuplicateGroups[i] = new DuplicateFileGroup { SizeBytes = group.SizeBytes, Files = remaining };
                }
                UpdateDuplicatesSelectionText();
            },
            loc.Get("Cleanup_DeletingBegin"),
            loc.Get("Common_Done"),
            loc.Get("Common_Cancelled"),
            confirm: (loc.Get("Cleanup_DeleteConfirmTitle"),
                string.Format(loc.Get("Cleanup_DeleteConfirmMessageFormat"), selectedPaths.Count, Environment.NewLine)));
    }
}
