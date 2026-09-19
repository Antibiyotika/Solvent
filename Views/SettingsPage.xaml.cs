using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using SolventUI.Models;
using SolventUI.Services;

namespace SolventUI.Views;

public partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadCurrentSettings();
    }

    private void LoadCurrentSettings()
    {
        var settings = SettingsService.Current;

        ThemeDarkRadio.IsChecked = settings.ThemeMode == AppThemeMode.Dark;
        ThemeLightRadio.IsChecked = settings.ThemeMode == AppThemeMode.Light;
        ThemeAutoRadio.IsChecked = settings.ThemeMode == AppThemeMode.System;

        LanguageEnglishRadio.IsChecked = settings.Language == "en";
        LanguageTurkishRadio.IsChecked = settings.Language == "tr";

        RunInBackgroundToggle.IsChecked = settings.RunInBackground;
        // The startup entry can also be toggled/removed from the Startup
        // Programs list (it's the same HKCU Run key) — re-read it from the
        // registry rather than trusting the settings file, so this switch
        // never gets out of sync with what actually happens at logon.
        LaunchAtStartupToggle.IsChecked = StartupLaunchService.IsEnabled();
    }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        var themeMode = ThemeAutoRadio.IsChecked == true ? AppThemeMode.System
            : ThemeLightRadio.IsChecked == true ? AppThemeMode.Light
            : AppThemeMode.Dark;
        var language = LanguageTurkishRadio.IsChecked == true ? "tr" : "en";
        var runInBackground = RunInBackgroundToggle.IsChecked == true;
        var launchAtStartup = LaunchAtStartupToggle.IsChecked == true;

        var settings = new AppSettings
        {
            ThemeMode = themeMode,
            Language = language,
            RunInBackground = runInBackground,
            LaunchAtStartup = launchAtStartup
        };
        SettingsService.Save(settings);
        StartupLaunchService.SetEnabled(launchAtStartup);

        if (runInBackground)
            TrayIconService.Instance.Show();
        else
            TrayIconService.Instance.Hide();

        // Apply behind a brief crossfade instead of an instant snap — see
        // MainWindow.PlayTransition. Falls back to applying immediately
        // if for some reason the main window isn't available.
        void Apply()
        {
            ThemeService.Apply(themeMode);
            LocalizationService.Instance.SetLanguage(language);
            LogService.Instance.Success(LocalizationService.Instance.Get("Settings_Saved"));
        }

        if (System.Windows.Application.Current.MainWindow is MainWindow mainWindow)
            mainWindow.PlayTransition(Apply);
        else
            Apply();

        ShowSavedConfirmation();
    }

    private void ShowSavedConfirmation()
    {
        var fadeIn = new DoubleAnimation(1, TimeSpan.FromMilliseconds(150));
        var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(400))
        {
            BeginTime = TimeSpan.FromSeconds(1.6)
        };
        var storyboard = new Storyboard();
        storyboard.Children.Add(fadeIn);
        storyboard.Children.Add(fadeOut);
        Storyboard.SetTarget(fadeIn, SavedText);
        Storyboard.SetTarget(fadeOut, SavedText);
        Storyboard.SetTargetProperty(fadeIn, new PropertyPath(UIElement.OpacityProperty));
        Storyboard.SetTargetProperty(fadeOut, new PropertyPath(UIElement.OpacityProperty));
        storyboard.Begin();
    }
}
