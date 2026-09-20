using SolventUI.Models;

namespace SolventUI.Services;

/// <summary>
/// What Solvent.exe was asked to do on the command line.
///
/// <code>
/// Solvent.exe                     open the window (default)
/// Solvent.exe /autoclean          no window: run the safe cleanup categories, then exit
/// Solvent.exe /autoclean /silent  same, without the completion notification
/// </code>
///
/// <c>/autoclean</c> is what the Schedule page's Task Scheduler entry
/// launches (see <see cref="ScheduleManagerService"/>). Options may also be
/// written <c>-autoclean</c> or <c>--autoclean</c>, in any letter case.
/// </summary>
public sealed record StartupOptions(bool AutoClean, bool Silent)
{
    public static StartupOptions Parse(IEnumerable<string> args)
    {
        var autoClean = false;
        var silent = false;

        foreach (var arg in args)
        {
            switch (arg.TrimStart('/', '-').ToLowerInvariant())
            {
                case "autoclean": autoClean = true; break;
                case "silent": silent = true; break;
            }
        }

        return new StartupOptions(autoClean, silent);
    }
}

/// <summary>
/// The window-less run behind <c>Solvent.exe /autoclean</c>: cleans the same
/// "safe defaults" categories as the Cleanup page's pre-checked boxes (never
/// the cautious ones, such as crash dumps), records the result in the
/// cleanup history, and optionally shows a tray notification.
///
/// Until this existed, the scheduled task started Solvent with
/// <c>/autoclean</c> but nothing read the flag, so the "automatic cleanup"
/// just opened the app window at the scheduled time and cleaned nothing.
/// </summary>
public sealed class AutoCleanRunner
{
    /// <summary>How long the process stays alive so the balloon notification can actually be seen.</summary>
    private static readonly TimeSpan NotificationLinger = TimeSpan.FromSeconds(6);

    private readonly CleanupHistoryService _history;

    public AutoCleanRunner(CleanupHistoryService history) => _history = history;

    /// <returns>The process exit code: 0 when the cleanup ran, 1 when it failed outright.</returns>
    public async Task<int> RunAsync(bool silent, CancellationToken ct = default)
    {
        var loc = LocalizationService.Instance;
        try
        {
            LogService.Instance.Info("Auto-clean: starting scheduled cleanup (safe categories only)...");
            var result = await CleanupScanService.CleanSelectedAsync(CleanupScanService.SafeDefaultIds, null, ct);
            _history.Record(result, CleanupHistoryService.SourceScheduled);

            if (!silent)
                await NotifyAsync(loc.Get("AutoClean_Title"),
                    string.Format(loc.Get("AutoClean_DoneFormat"), result.FreedText));
            return 0;
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Auto-clean failed: {ex.Message}");
            if (!silent)
                await NotifyAsync(loc.Get("AutoClean_Title"), loc.Get("AutoClean_FailedMessage"));
            return 1;
        }
    }

    private static async Task NotifyAsync(string title, string message)
    {
        try
        {
            var tray = TrayIconService.Instance;
            tray.Show();
            tray.Notify(title, message);
            await Task.Delay(NotificationLinger);
        }
        catch (Exception ex)
        {
            // A missing notification must never turn a finished cleanup into a failure.
            LogService.Instance.Warning($"Could not show the completion notification: {ex.Message}");
        }
    }
}
