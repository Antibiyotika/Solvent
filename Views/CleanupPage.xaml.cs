using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SolventUI.Models;
using SolventUI.Services;

namespace SolventUI.Views;

public partial class CleanupPage : Page
{
    private CancellationTokenSource? _cts;
    private readonly ObservableCollection<DuplicateFileGroup> _duplicateGroups = new();
    private readonly ObservableCollection<CleanupCategory> _categories = new();
    private long _largeFilesThreshold = 50 * 1024 * 1024;

    public CleanupPage()
    {
        InitializeComponent();
        DuplicatesList.ItemsSource = _duplicateGroups;
        CategoriesList.ItemsSource = _categories;
    }

    // ---------- Deep Clean (scan → select → clean) ----------

    private async void Scan_OnClick(object sender, RoutedEventArgs e)
    {
        var loc = LocalizationService.Instance;
        ScanButton.IsEnabled = false;
        _cts = new CancellationTokenSource();
        ProgressPanel.Begin(loc.Get("Cleanup_ScanBegin"));

        try
        {
            var result = await CleanupScanService.ScanAsync(_cts.Token);
            _categories.Clear();
            foreach (var category in result.Categories) _categories.Add(category);

            ScanToolbar.Visibility = Visibility.Visible;
            CategoriesScroll.Visibility = Visibility.Visible;
            CleanBar.Visibility = Visibility.Visible;
            UpdateCategorySelectionText();
            ProgressPanel.Finish(loc.Get("Common_Done"));
        }
        catch (OperationCanceledException)
        {
            ProgressPanel.Finish(loc.Get("Common_Cancelled"));
        }
        finally
        {
            ScanButton.IsEnabled = true;
            _cts.Dispose();
            _cts = null;
            await Task.Delay(1200);
            ProgressPanel.Hide();
        }
    }

    private void SelectAllCategories_OnClick(object sender, RoutedEventArgs e) => SetAllCategorySelection(true);
    private void SelectNoneCategories_OnClick(object sender, RoutedEventArgs e) => SetAllCategorySelection(false);

    private void SetAllCategorySelection(bool selected)
    {
        foreach (var category in _categories) category.IsSelected = selected;
        // CleanupCategory isn't INotifyPropertyChanged (same tradeoff as
        // DuplicateFileEntry) — force the ItemsControl to re-read the list.
        CategoriesList.ItemsSource = null;
        CategoriesList.ItemsSource = _categories;
        UpdateCategorySelectionText();
    }

    private void UpdateCategorySelectionText()
    {
        var loc = LocalizationService.Instance;
        var total = _categories.Sum(c => c.SizeBytes);
        ScanTotalText.Text = string.Format(loc.Get("Cleanup_ScanTotalFormat"), SizeFormat.Format(total));

        var selected = _categories.Where(c => c.IsSelected).ToList();
        var selectedBytes = selected.Sum(c => c.SizeBytes);
        SelectionSummaryText.Text = selected.Count == 0
            ? loc.Get("Cleanup_NothingSelected")
            : string.Format(loc.Get("Cleanup_CategorySelectedFormat"), selected.Count, SizeFormat.Format(selectedBytes));
    }

    private async void CleanSelected_OnClick(object sender, RoutedEventArgs e)
    {
        var loc = LocalizationService.Instance;
        var selected = _categories.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0) return;

        var hasCaution = selected.Any(c => c.Risk == CleanupRisk.Caution);
        var confirmMessage = string.Format(
            loc.Get(hasCaution ? "Cleanup_CleanSelectedCautionConfirmMessage" : "Cleanup_CleanSelectedConfirmMessage"),
            SizeFormat.Format(selected.Sum(c => c.SizeBytes)), Environment.NewLine);

        if (!ConfirmationService.Ask(loc.Get("Cleanup_CleanConfirmTitle"), confirmMessage))
            return;

        CleanSelectedButton.IsEnabled = false;
        _cts = new CancellationTokenSource();
        ProgressPanel.Begin(loc.Get("Cleanup_CleanBegin"));
        var reporter = new Progress<TaskProgress>(ProgressPanel.Report);
        var ids = selected.Select(c => c.Id).ToList();

        try
        {
            var result = await CleanupScanService.CleanSelectedAsync(ids, reporter, _cts.Token);
            ProgressPanel.Finish(string.Format(loc.Get("Cleanup_FreedFormat"), result.FreedText));

            // Re-scan so the list reflects what's actually left (0 bytes for
            // whatever just got cleaned) instead of showing stale numbers.
            var rescan = await CleanupScanService.ScanAsync(_cts.Token);
            _categories.Clear();
            foreach (var category in rescan.Categories) _categories.Add(category);
            UpdateCategorySelectionText();
        }
        catch (OperationCanceledException)
        {
            ProgressPanel.Finish(loc.Get("Common_Cancelled"));
        }
        finally
        {
            CleanSelectedButton.IsEnabled = true;
            _cts.Dispose();
            _cts = null;
            await Task.Delay(1600);
            ProgressPanel.Hide();
        }
    }

    // ---------- Quick-access cards ----------

    private void StartupCard_OnClick(object sender, MouseButtonEventArgs e) =>
        NavigationService?.Navigate(new StartupManagerPage());

    private void ScheduleCard_OnClick(object sender, MouseButtonEventArgs e) =>
        NavigationService?.Navigate(new ScheduleManagerPage());

    // ---------- Large Files ----------

    private void ThresholdCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThresholdCombo.SelectedItem is ComboBoxItem { Tag: string tagText } && long.TryParse(tagText, out var bytes))
            _largeFilesThreshold = bytes;

        // Only re-run the scan if the panel is already open and showing a
        // previous result — changing the threshold before ever scanning
        // just sets the default for the first scan.
        if (LargeFilesPanel.Visibility == Visibility.Visible && LargeFilesList.ItemsSource != null)
            _ = RunLargeFilesScanAsync();
    }

    private async void LargeFiles_OnClick(object sender, MouseButtonEventArgs e)
    {
        LargeFilesPanel.Visibility = Visibility.Visible;
        await RunLargeFilesScanAsync();
    }

    private async Task RunLargeFilesScanAsync()
    {
        var loc = LocalizationService.Instance;
        LargeFilesList.ItemsSource = null;

        _cts = new CancellationTokenSource();
        ProgressPanel.Begin(loc.Get("Cleanup_LargeFilesBegin"));
        try
        {
            var results = await TaskService.FindLargeFilesAsync(minSizeBytes: _largeFilesThreshold, ct: _cts.Token);
            LargeFilesList.ItemsSource = results;
            ProgressPanel.Finish(loc.Get("Common_Done"));
        }
        catch (OperationCanceledException)
        {
            ProgressPanel.Finish(loc.Get("Common_Cancelled"));
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            await Task.Delay(1200);
            ProgressPanel.Hide();
        }
    }

    private void OpenFolder_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: LargeFileInfo file }) return;
        try
        {
            Process.Start("explorer.exe", $"/select,\"{file.FullPath}\"");
        }
        catch { /* best-effort — nothing to recover from here */ }
    }

    // ---------- Duplicate Files ----------

    private async void Duplicates_OnClick(object sender, MouseButtonEventArgs e)
    {
        var loc = LocalizationService.Instance;
        DuplicatesPanel.Visibility = Visibility.Visible;
        _duplicateGroups.Clear();
        DuplicatesSummary.Text = loc.Get("Cleanup_DuplicatesDefaultSummary");
        UpdateDuplicatesSelectionText();

        _cts = new CancellationTokenSource();
        ProgressPanel.Begin(loc.Get("Cleanup_DuplicatesBegin"));
        var reporter = new Progress<TaskProgress>(ProgressPanel.Report);

        try
        {
            var groups = await TaskService.FindDuplicateFilesAsync(reporter, _cts.Token);
            foreach (var g in groups) _duplicateGroups.Add(g);

            var wastedBytes = groups.Sum(g => g.SizeBytes * (g.Files.Count - 1));
            DuplicatesSummary.Text = groups.Count == 0
                ? loc.Get("Cleanup_NoDuplicates")
                : string.Format(loc.Get("Cleanup_DuplicatesFoundFormat"), groups.Count, SizeFormat.Format(wastedBytes));
            UpdateDuplicatesSelectionText();
            ProgressPanel.Finish(loc.Get("Common_Done"));
        }
        catch (OperationCanceledException)
        {
            ProgressPanel.Finish(loc.Get("Common_Cancelled"));
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            await Task.Delay(1200);
            ProgressPanel.Hide();
        }
    }

    private void SelectAllDuplicates_OnClick(object sender, RoutedEventArgs e) => SetAllDuplicateSelection(true);
    private void SelectNoneDuplicates_OnClick(object sender, RoutedEventArgs e) => SetAllDuplicateSelection(false);

    private void SetAllDuplicateSelection(bool selected)
    {
        foreach (var group in _duplicateGroups)
            foreach (var file in group.Files)
                file.IsSelected = selected;

        // IsSelected isn't INotifyPropertyChanged, so force the ItemsControl
        // to re-read the (now bulk-updated) collection.
        DuplicatesList.ItemsSource = null;
        DuplicatesList.ItemsSource = _duplicateGroups;
        UpdateDuplicatesSelectionText();
    }

    private void UpdateDuplicatesSelectionText()
    {
        var loc = LocalizationService.Instance;
        var selected = _duplicateGroups.SelectMany(g => g.Files.Select(f => (f, g.SizeBytes))).Where(x => x.f.IsSelected).ToList();
        var bytes = selected.Sum(x => x.SizeBytes);
        DuplicatesSelectionText.Text = selected.Count == 0
            ? loc.Get("Cleanup_NothingSelected")
            : string.Format(loc.Get("Cleanup_SelectedFormat"), selected.Count, SizeFormat.Format(bytes));
    }

    private async void DeleteDuplicates_OnClick(object sender, RoutedEventArgs e)
    {
        var loc = LocalizationService.Instance;
        var selectedPaths = _duplicateGroups.SelectMany(g => g.Files).Where(f => f.IsSelected).Select(f => f.FullPath).ToList();
        if (selectedPaths.Count == 0) return;

        if (!ConfirmationService.Ask(
                loc.Get("Cleanup_DeleteConfirmTitle"),
                string.Format(loc.Get("Cleanup_DeleteConfirmMessageFormat"), selectedPaths.Count, Environment.NewLine)))
            return;

        DeleteDuplicatesButton.IsEnabled = false;
        _cts = new CancellationTokenSource();
        ProgressPanel.Begin(loc.Get("Cleanup_DeletingBegin"));

        try
        {
            await TaskService.DeleteFilesToRecycleBinAsync(selectedPaths, _cts.Token);
            ProgressPanel.Finish(loc.Get("Common_Done"));

            // Drop deleted files from the displayed groups — a group with
            // one (or zero) files left no longer represents a duplicate.
            foreach (var group in _duplicateGroups.ToList())
            {
                group.Files.RemoveAll(f => f.IsSelected);
                if (group.Files.Count <= 1)
                    _duplicateGroups.Remove(group);
            }
            DuplicatesList.ItemsSource = null;
            DuplicatesList.ItemsSource = _duplicateGroups;
            UpdateDuplicatesSelectionText();
        }
        catch (OperationCanceledException)
        {
            ProgressPanel.Finish(loc.Get("Common_Cancelled"));
        }
        finally
        {
            DeleteDuplicatesButton.IsEnabled = true;
            _cts.Dispose();
            _cts = null;
            await Task.Delay(1200);
            ProgressPanel.Hide();
        }
    }

    private void Progress_OnCancelRequested(object? sender, EventArgs e) => _cts?.Cancel();
}
