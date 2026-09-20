using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using SolventUI.Models;
using SolventUI.Services;
using SolventUI.ViewModels;

namespace SolventUI;

public partial class App : Application
{
    /// <summary>
    /// The app's DI container. Used almost entirely through
    /// <see cref="ControlsServices"/> below — WPF-UI's NavigationView
    /// resolves a page's constructor parameters from it automatically, so a
    /// page just declares <c>public DashboardPage(DashboardViewModel vm)</c>
    /// and gets one. Exposed here only for the rare case something needs to
    /// resolve a service directly instead of through a page constructor.
    /// </summary>
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();
        // Lets WPF-UI's NavigationView construct pages via DI (see
        // NavigationViewActivator in the WPF-UI source): a page with a
        // constructor parameter instead of a parameterless one gets that
        // parameter resolved from this provider. This must happen before
        // MainWindow's NavigationView first navigates, which — since
        // StartupUri creates MainWindow only after OnStartup fully returns
        // (WPF's Application.DoStartup runs after OnStartup) — it always
        // does when set here.
        ControlsServices.Initialize(Services);

        // Global safety nets: a single failed repair/security action (a process
        // that fails to start, an unexpected exit code, etc.) should be logged
        // and reported, never take down the whole app. Previously several
        // TaskService methods (e.g. RepairSystemErrorsAsync) had no try/catch
        // of their own and there was no fallback here either, so any exception
        // from an async void Click handler crashed the process outright.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Live-follow Windows' own dark/light setting when the user has
        // Solvent's theme set to "Auto" (AppThemeMode.System) — see
        // ThemeService.DetectSystemTheme and SettingsPage's third radio.
        SystemEvents.UserPreferenceChanged += OnSystemThemeMaybeChanged;

        // Make sure the tray icon (if it was ever shown because "Run in
        // background" is on) never lingers after the process is actually
        // gone — Exit fires once for a real shutdown, unlike hiding the
        // window to the tray, which never gets here.
        Exit += (_, _) =>
        {
            SystemEvents.UserPreferenceChanged -= OnSystemThemeMaybeChanged;
            TrayIconService.Instance.Dispose();
        };

        // Load the user's saved theme/language (creates %AppData%\Solvent\
        // settings.json with defaults on first run) and apply both before
        // MainWindow is constructed, so it opens already in the right
        // state instead of flashing the default and then switching.
        var settings = SettingsService.Load();
        ThemeService.Apply(settings.ThemeMode);
        LocalizationService.Instance.Initialize(settings.Language);
    }

    /// <summary>
    /// Windows raises this (Category: General, among others) when the user
    /// flips Settings > Personalization > Colors > app mode, plus a handful
    /// of unrelated preference changes — cheap enough to just re-resolve
    /// and re-apply the theme on every General notification rather than
    /// trying to filter more precisely. Only matters when Solvent's own
    /// theme is set to "Auto"; an explicit Dark/Light choice never changes
    /// because of this.
    /// </summary>
    private void OnSystemThemeMaybeChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        if (SettingsService.Current.ThemeMode != AppThemeMode.System) return;
        Dispatcher.Invoke(() => ThemeService.Apply(AppThemeMode.System));
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var root = Unwrap(e.Exception);
        LogService.Instance.Error($"Unexpected error: {root.Message}");
        System.Windows.MessageBox.Show(
            string.Format(
                LocalizationService.Instance.Get("App_ErrorMessageFormat"),
                Environment.NewLine, root.GetType().Name, root.Message),
            "Solvent",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Warning);
        e.Handled = true; // keep the app alive instead of crashing
    }

    /// <summary>
    /// Reflection-based calls (e.g. WPF-UI's NavigationView constructing a
    /// page via ConstructorInfo.Invoke) wrap the real exception inside a
    /// TargetInvocationException whose own Message is just the generic
    /// "Exception has been thrown by the target of an invocation." text.
    /// Walk down to the actual cause so error messages are useful.
    /// </summary>
    private static Exception Unwrap(Exception ex)
    {
        while (ex is System.Reflection.TargetInvocationException { InnerException: { } inner })
            ex = inner;
        return ex;
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            LogService.Instance.Error($"Fatal error: {Unwrap(ex).Message}");
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogService.Instance.Error($"Background task error: {e.Exception.Message}");
        e.SetObserved();
    }

    /// <summary>
    /// View model registrations. Transient, not singleton: each navigation
    /// to a page should get a fresh view model (and, for Dashboard, a fresh
    /// live-resource timer started/stopped with that specific page
    /// instance's lifetime) rather than sharing state across visits.
    ///
    /// The static *Service classes in Services/ (SystemInfoService,
    /// TaskService, ConfirmationService, LocalizationService.Instance, ...)
    /// are left as they are here — they're stateless facades over OS calls
    /// or already-singleton by design, so wrapping each in an interface
    /// just to satisfy DI would add ceremony without changing behavior.
    /// View models call them directly, as this one does.
    /// </summary>
    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<CleanupViewModel>();
        services.AddTransient<PerformanceViewModel>();
        services.AddTransient<RepairSecurityViewModel>();
        services.AddTransient<DiagnosticsViewModel>();
        services.AddTransient<StartupManagerViewModel>();
        services.AddTransient<ScheduleManagerViewModel>();
        services.AddTransient<SettingsViewModel>();
    }
}
