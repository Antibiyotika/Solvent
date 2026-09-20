using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolventUI.Models;
using SolventUI.Services;

namespace SolventUI.ViewModels;

/// <summary>
/// Startup Manager: load + best-effort publisher/signature enrichment,
/// instant client-side search filtering, per-item enable toggle, and
/// remove / remove-all-missing. <see cref="StartupItem"/> being observable
/// (see its own doc comment) is what lets enrichment update the Publisher
/// column in place — this view model just needs to mutate the items it
/// already has, no manual list-rebinding.
/// </summary>
public sealed partial class StartupManagerViewModel : ViewModelBase
{
    private List<StartupItem> _allItems = new();

    public ObservableCollection<StartupItem> Items { get; } = new();

    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private bool hasMissingItems;
    [ObservableProperty] private string removeMissingText = "";

    /// <summary>CommunityToolkit-generated hook for the SearchText property — re-filters on every keystroke, no rescan.</summary>
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    public async Task LoadItemsAsync()
    {
        var loc = LocalizationService.Instance;
        await RunOperationAsync(
            async (progress, ct) =>
            {
                _allItems = StartupManagerService.GetStartupItems();
                ApplyFilter(); // show names immediately — publisher/signature fills in a moment later

                progress.Report(TaskProgress.Busy(loc.Get("Startup_CheckingSignatures")));
                await StartupManagerService.EnrichWithPublisherInfoAsync(_allItems, ct);
                UpdateMissingSummary();
            },
            loc.Get("Startup_LoadingBegin"), loc.Get("Common_Done"), loc.Get("Common_Cancelled"));
    }

    /// <summary>Filters the already-loaded list by name or command — no rescan, so typing stays instant even with a couple hundred startup entries.</summary>
    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        var filtered = string.IsNullOrEmpty(query)
            ? _allItems
            : _allItems.Where(i =>
                    i.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    i.Command.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

        Items.Clear();
        foreach (var item in filtered) Items.Add(item);
    }

    private void UpdateMissingSummary()
    {
        var loc = LocalizationService.Instance;
        var missing = _allItems.Count(i => !i.FileExists);
        HasMissingItems = missing > 0;
        RemoveMissingText = string.Format(loc.Get("Startup_RemoveMissingFormat"), missing);
    }

    /// <summary>Called from the page's ToggleSwitch Checked/Unchecked — the switch already wrote IsEnabled via its TwoWay binding; this just persists that to the registry/startup folder.</summary>
    public void PersistEnabledState(StartupItem item)
    {
        StartupManagerService.SetEnabled(item, item.IsEnabled);
        LogService.Instance.Info($"Startup: {item.Name} {(item.IsEnabled ? "enabled" : "disabled")}.");
    }

    [RelayCommand]
    private void RemoveItem(StartupItem? item)
    {
        if (item is null) return;
        StartupManagerService.Remove(item);
        LogService.Instance.Info($"Startup: removed {item.Name}.");
        _allItems.Remove(item);
        ApplyFilter();
        UpdateMissingSummary();
    }

    [RelayCommand]
    private void RemoveMissing()
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
}
