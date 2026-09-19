using System.Windows;
using System.Windows.Controls;
using SolventUI.Models;
using SolventUI.Services;

namespace SolventUI.Views;

public partial class StartupManagerPage : Page
{
    private List<StartupItem> _allItems = new();
    private CancellationTokenSource? _cts;

    public StartupManagerPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadItemsAsync();
    }

    private async Task LoadItemsAsync()
    {
        RefreshButton.IsEnabled = false;
        RemoveMissingButton.IsEnabled = false;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var loc = LocalizationService.Instance;
        ProgressPanel.Begin(loc.Get("Startup_LoadingBegin"));

        try
        {
            _allItems = StartupManagerService.GetStartupItems();
            ApplyFilter(); // show names immediately — publisher/signature fills in a moment later

            ProgressPanel.Report(TaskProgress.Busy(loc.Get("Startup_CheckingSignatures")));
            await StartupManagerService.EnrichWithPublisherInfoAsync(_allItems, ct);
            ApplyFilter(); // re-bind so the Publisher column reflects the enrichment
            UpdateMissingSummary();
            ProgressPanel.Finish(loc.Get("Common_Done"));
        }
        catch (OperationCanceledException)
        {
            ProgressPanel.Finish(loc.Get("Common_Cancelled"));
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            RemoveMissingButton.IsEnabled = true;
            await Task.Delay(800);
            ProgressPanel.Hide();
        }
    }

    private async void Refresh_OnClick(object sender, RoutedEventArgs e) => await LoadItemsAsync();

    /// <summary>
    /// Filters the already-loaded list by name or command — no rescan, so
    /// typing stays instant even with a couple hundred startup entries.
    /// </summary>
    private void SearchBox_OnTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var query = SearchBox.Text?.Trim();
        ItemsList.ItemsSource = null; // StartupItem isn't INotifyPropertyChanged — force a fresh bind so enrichment shows up
        ItemsList.ItemsSource = string.IsNullOrEmpty(query)
            ? _allItems
            : _allItems.Where(i =>
                    i.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    i.Command.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
    }

    private void UpdateMissingSummary()
    {
        var loc = LocalizationService.Instance;
        var missing = _allItems.Count(i => !i.FileExists);
        RemoveMissingButton.Visibility = missing > 0 ? Visibility.Visible : Visibility.Collapsed;
        RemoveMissingButton.Content = string.Format(loc.Get("Startup_RemoveMissingFormat"), missing);
    }

    private void Toggle_OnChanged(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StartupItem item })
        {
            StartupManagerService.SetEnabled(item, item.IsEnabled);
            LogService.Instance.Info($"Startup: {item.Name} {(item.IsEnabled ? "enabled" : "disabled")}.");
        }
    }

    private void Remove_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: StartupItem item }) return;

        StartupManagerService.Remove(item);
        LogService.Instance.Info($"Startup: removed {item.Name}.");
        _allItems.Remove(item);
        ApplyFilter();
        UpdateMissingSummary();
    }

    private void RemoveMissing_OnClick(object sender, RoutedEventArgs e)
    {
        var loc = LocalizationService.Instance;
        var missing = _allItems.Where(i => !i.FileExists).ToList();
        if (missing.Count == 0) return;

        if (!ConfirmationService.Ask(
                loc.Get("Startup_RemoveMissingConfirmTitle"),
                string.Format(loc.Get("Startup_RemoveMissingConfirmMessageFormat"), missing.Count, Environment.NewLine)))
            return;

        foreach (var item in missing)
        {
            StartupManagerService.Remove(item);
            LogService.Instance.Info($"Startup: removed {item.Name} (file no longer exists).");
            _allItems.Remove(item);
        }
        ApplyFilter();
        UpdateMissingSummary();
    }

    private void Progress_OnCancelRequested(object? sender, EventArgs e) => _cts?.Cancel();
}
