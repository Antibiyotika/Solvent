using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Animation;
using SolventUI.Models;
using SolventUI.Services;
using Wpf.Ui.Controls;

namespace SolventUI;

public partial class MainWindow : FluentWindow
{
    private CancellationTokenSource? _runAllCts;

    private readonly bool _isAdmin;

    /// <summary>Set right before a deliberate Shutdown() (tray "Exit") so OnClosing lets the close through instead of hiding to tray.</summary>
    private bool _isExiting;

    /// <summary>Only show the "still running in the background" balloon once per session, not on every close-to-tray.</summary>
    private bool _hasShownTrayHint;

    public MainWindow()
    {
        InitializeComponent();

        _isAdmin = SystemInfoService.GetSystemInfo().IsAdmin;
        UpdateAdminBadge();
        LocalizationService.Instance.LanguageChanged += UpdateAdminBadge;

        TrayIconService.Instance.OpenRequested += OnTrayOpenRequested;
        TrayIconService.Instance.RunAllRequested += OnTrayRunAllRequested;
        TrayIconService.Instance.ExitRequested += OnTrayExitRequested;
        if (SettingsService.Current.RunInBackground)
            TrayIconService.Instance.Show();

        Closed += (_, _) =>
        {
            LocalizationService.Instance.LanguageChanged -= UpdateAdminBadge;
            TrayIconService.Instance.OpenRequested -= OnTrayOpenRequested;
            TrayIconService.Instance.RunAllRequested -= OnTrayRunAllRequested;
            TrayIconService.Instance.ExitRequested -= OnTrayExitRequested;
        };
    }

    /// <summary>
    /// When "Run in background" is on, the X button hides the window to
    /// the tray instead of exiting — the tray icon's own "Exit" item (which
    /// sets <see cref="_isExiting"/> first) is the only way out. Behaves
    /// exactly as before when the setting is off.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (SettingsService.Current.RunInBackground && !_isExiting)
        {
            e.Cancel = true;
            Hide();

            if (!_hasShownTrayHint)
            {
                _hasShownTrayHint = true;
                var loc = LocalizationService.Instance;
                TrayIconService.Instance.Notify(loc.Get("App_WindowTitle"), loc.Get("Tray_StillRunningMessage"));
            }
            return;
        }

        base.OnClosing(e);
    }

    private void OnTrayOpenRequested() => Dispatcher.Invoke(() =>
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    });

    private async void OnTrayRunAllRequested()
    {
        OnTrayOpenRequested();
        await RunAllInternalAsync();
    }

    private void OnTrayExitRequested()
    {
        _isExiting = true;
        Dispatcher.Invoke(() => System.Windows.Application.Current.Shutdown());
    }

    /// <summary>
    /// A genuine quit-and-relaunch, used by SettingsPage after an accent
    /// color change (see OfferAccentRestart there). Needs the same
    /// <see cref="_isExiting"/> flag the tray icon's own Exit item uses —
    /// otherwise, with "Run in background" on, OnClosing would just hide
    /// this window to the tray instead of letting Shutdown() through,
    /// leaving the freshly-launched process to run alongside this one
    /// instead of replacing it.
    /// </summary>
    public void ExitForRestart()
    {
        _isExiting = true;
        System.Windows.Application.Current.Shutdown();
    }

    private void UpdateAdminBadge()
    {
        AdminBadge.Text = LocalizationService.Instance.Get(
            _isAdmin ? "Titlebar_Admin" : "Titlebar_NotElevated");
        AdminBadge.Foreground = _isAdmin
            ? (System.Windows.Media.Brush)FindResource("SolventAccentBrush")
            : (System.Windows.Media.Brush)FindResource("SolventWarningBrush");
    }

    /// <summary>
    /// Runs <paramref name="applyChanges"/> behind a brief crossfade instead
    /// of letting the theme/language swap happen instantly under the
    /// user's eyes: fades <see cref="ThemeTransitionOverlay"/> in (to the
    /// *current* background color), applies the change while the overlay
    /// is fully opaque, then fades it back out over the *new* background.
    /// Used by SettingsPage after Save.
    /// </summary>
    public void PlayTransition(Action applyChanges)
    {
        const int fadeInMs = 160;
        const int fadeOutMs = 320;

        var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(fadeInMs))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        fadeIn.Completed += (_, _) =>
        {
            applyChanges();

            var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(fadeOutMs))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            ThemeTransitionOverlay.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        };
        ThemeTransitionOverlay.BeginAnimation(UIElement.OpacityProperty, fadeIn);
    }

    private void RootNavigation_OnLoaded(object sender, RoutedEventArgs e)
    {
        // Land on the Dashboard the first time the shell opens.
        RootNavigation.Navigate(typeof(Views.DashboardPage));
    }

    private async void RunAll_OnClick(object sender, RoutedEventArgs e) => await RunAllInternalAsync();

    /// <summary>
    /// The actual "Run All" flow, shared by the footer nav button and the
    /// tray icon's "Run All Maintenance" item — previously only the click
    /// handler had this logic, so triggering it from the tray meant
    /// duplicating (and inevitably drifting from) the same steps.
    /// </summary>
    private async Task RunAllInternalAsync()
    {
        var loc = LocalizationService.Instance;
        if (!ConfirmationService.Ask(
                loc.Get("Dashboard_ConfirmTitle"),
                string.Format(loc.Get("Dashboard_ConfirmMessage"), Environment.NewLine)))
            return;

        RootNavigation.Navigate(typeof(Views.DashboardPage));

        _runAllCts = new CancellationTokenSource();
        RunAllProgress.Begin(loc.Get("Dashboard_RunAllBegin"));
        var reporter = new Progress<TaskProgress>(RunAllProgress.Report);

        try
        {
            await TaskService.RunAllAsync(reporter, _runAllCts.Token);
            RunAllProgress.Finish(loc.Get("Dashboard_RunAllDone"));

            // The window may be hidden to the tray (background run) — the
            // balloon is the only way the user finds out it's done.
            if (!IsVisible)
                TrayIconService.Instance.Notify(loc.Get("App_WindowTitle"), loc.Get("Dashboard_RunAllDone"));
        }
        catch (OperationCanceledException)
        {
            RunAllProgress.Finish(loc.Get("Dashboard_RunAllCancelled"));
        }
        finally
        {
            _runAllCts.Dispose();
            _runAllCts = null;
            await Task.Delay(2000);
            RunAllProgress.Hide();
        }
    }

    private void RunAllProgress_OnCancelRequested(object? sender, EventArgs e) => _runAllCts?.Cancel();
}
