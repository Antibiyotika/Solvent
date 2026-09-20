using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using SolventUI.Services;
using SolventUI.ViewModels;

namespace SolventUI.Views;

/// <summary>
/// View half of Settings: every preference binds straight to
/// <see cref="SettingsViewModel"/>. What stays here is what genuinely needs
/// a real window or a Storyboard: playing the theme-change crossfade,
/// showing the "Saved" fade animation, and offering the optional restart
/// after an accent change.
/// </summary>
public partial class SettingsPage : Page
{
    private readonly SettingsViewModel _viewModel;

    public SettingsPage(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;

        viewModel.SettingsApplied += OnSettingsApplied;
        viewModel.AccentChangeOffered += OnAccentChangeOffered;
        Unloaded += (_, _) =>
        {
            viewModel.SettingsApplied -= OnSettingsApplied;
            viewModel.AccentChangeOffered -= OnAccentChangeOffered;
        };
    }

    private void AccentSwatch_OnClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AccentOption option })
            _viewModel.SelectAccentCommand.Execute(option);
    }

    private void OnSettingsApplied()
    {
        // Apply behind a brief crossfade instead of an instant snap — see
        // MainWindow.PlayTransition. Falls back to applying immediately
        // if for some reason the main window isn't available.
        if (Application.Current.MainWindow is MainWindow mainWindow)
            mainWindow.PlayTransition(_viewModel.ApplyThemeAndLanguage);
        else
            _viewModel.ApplyThemeAndLanguage();

        ShowSavedConfirmation();
    }

    // The new accent's base color and every non-animated Background it
    // touches update immediately (they're plain DynamicResource lookups
    // WPF re-evaluates on the spot) — but a couple of Fluent controls'
    // hover/pressed states bake their color into a cached animation the
    // first time they're rendered, and only pick up a genuinely new
    // accent value after that cache is gone. A real process restart is
    // the only way to guarantee that's cleared, so it's offered as an
    // optional, explicit choice rather than done silently.
    private void OnAccentChangeOffered()
    {
        var loc = LocalizationService.Instance;
        if (!ConfirmationService.Ask(
                loc.Get("Settings_AccentRestartTitle"),
                string.Format(loc.Get("Settings_AccentRestartMessage"), Environment.NewLine)))
            return;

        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
                System.Diagnostics.Process.Start(exePath);
        }
        catch (Exception ex)
        {
            // Restarting is a convenience, not a requirement — the settings
            // themselves are already saved either way, so a failure here
            // (e.g. no permission to spawn a new process) just means the
            // user keeps using the current session instead of losing work.
            LogService.Instance.Error($"Could not restart Solvent: {ex.Message}");
            return;
        }

        // ExitForRestart (not a plain Shutdown()) — if "Run in background"
        // is on, a plain Shutdown() would just get cancelled by
        // MainWindow.OnClosing hiding to the tray instead, leaving two
        // copies of Solvent running side by side.
        if (Application.Current.MainWindow is MainWindow mainWindow)
            mainWindow.ExitForRestart();
        else
            Application.Current.Shutdown();
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
