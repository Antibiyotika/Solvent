using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolventUI.Models;
using SolventUI.Services;

namespace SolventUI.ViewModels;

/// <summary>One accent color swatch: its hex value and whether it's the current pick.</summary>
public sealed partial class AccentOption : ObservableObject
{
    public required string Hex { get; init; }

    [ObservableProperty]
    private bool isSelected;
}

/// <summary>
/// Settings page: theme, language, run-in-background, launch-at-startup,
/// the restore-point safety net, and the accent color picker. Saving here is deliberately synchronous
/// (writing a small JSON file and a couple of registry values), so this
/// stays a plain <see cref="ObservableObject"/> rather than <see cref="ViewModelBase"/>.
///
/// Two things after Save stay in <see cref="Views.SettingsPage"/> rather than
/// here: applying the new theme/language is meant to happen inside
/// MainWindow's crossfade transition, and offering a restart after an
/// accent change needs the actual MainWindow instance. This view model
/// raises <see cref="SettingsApplied"/> and <see cref="AccentChangeOffered"/>
/// for the page to act on, and exposes <see cref="ApplyThemeAndLanguage"/>
/// for the page to call from inside that transition.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty] private AppThemeMode themeMode;
    [ObservableProperty] private string language = "en";
    [ObservableProperty] private bool runInBackground;
    [ObservableProperty] private bool launchAtStartup;
    [ObservableProperty] private bool autoRestorePoint;

    public ObservableCollection<AccentOption> AccentOptions { get; } = new();

    public event Action? SettingsApplied;
    public event Action? AccentChangeOffered;

    public SettingsViewModel()
    {
        var settings = SettingsService.Current;
        ThemeMode = settings.ThemeMode;
        Language = settings.Language;
        RunInBackground = settings.RunInBackground;
        AutoRestorePoint = settings.AutoRestorePoint;
        // The startup entry can also be toggled/removed from the Startup
        // Programs list (it's the same HKCU Run key) — re-read it from the
        // registry rather than trusting the settings file, so this switch
        // never gets out of sync with what actually happens at logon.
        LaunchAtStartup = StartupLaunchService.IsEnabled();

        foreach (var hex in ThemeService.AccentPresets)
            AccentOptions.Add(new AccentOption
            {
                Hex = hex,
                IsSelected = string.Equals(hex, settings.AccentColorHex, StringComparison.OrdinalIgnoreCase),
            });
    }

    private string SelectedAccentHex => AccentOptions.FirstOrDefault(a => a.IsSelected)?.Hex ?? "#22C55E";

    [RelayCommand]
    private void SelectAccent(AccentOption? option)
    {
        if (option is null) return;
        foreach (var candidate in AccentOptions)
            candidate.IsSelected = ReferenceEquals(candidate, option);
    }

    [RelayCommand]
    private void Save()
    {
        // Captured before Save() overwrites SettingsService.Current, so we
        // can tell afterward whether the accent specifically changed.
        var accentChanged = !string.Equals(
            SettingsService.Current.AccentColorHex, SelectedAccentHex, StringComparison.OrdinalIgnoreCase);

        var settings = new AppSettings
        {
            ThemeMode = ThemeMode,
            Language = Language,
            RunInBackground = RunInBackground,
            LaunchAtStartup = LaunchAtStartup,
            AutoRestorePoint = AutoRestorePoint,
            AccentColorHex = SelectedAccentHex,
        };
        SettingsService.Save(settings);
        StartupLaunchService.SetEnabled(LaunchAtStartup);

        if (RunInBackground)
            TrayIconService.Instance.Show();
        else
            TrayIconService.Instance.Hide();

        SettingsApplied?.Invoke();
        if (accentChanged)
            AccentChangeOffered?.Invoke();
    }

    /// <summary>
    /// The actual theme/language flip — called by the page from inside its
    /// crossfade transition (or immediately, if no window is available)
    /// rather than eagerly here, so the switch happens mid-fade instead of
    /// as a jarring instant snap.
    /// </summary>
    public void ApplyThemeAndLanguage()
    {
        ThemeService.Apply(ThemeMode);
        LocalizationService.Instance.SetLanguage(Language);
        LogService.Instance.Success(LocalizationService.Instance.Get("Settings_Saved"));
    }
}
