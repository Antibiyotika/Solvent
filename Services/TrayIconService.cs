using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SolventUI.Services;

/// <summary>
/// The notification-area (system tray) icon shown while Solvent is set to
/// keep running in the background (<c>AppSettings.RunInBackground</c>,
/// Settings page). WPF has no native tray control, so this wraps
/// <see cref="System.Windows.Forms.NotifyIcon"/> — the standard,
/// dependency-free way to get one (enabled via
/// <c>&lt;UseWindowsForms&gt;</c> in the .csproj; nothing else about the
/// app becomes a WinForms app, this is the only class that touches it).
///
/// Lifecycle: <see cref="Show"/>/<see cref="Hide"/> are toggled whenever
/// the "Run in background" setting is saved (see SettingsPage) and once
/// at startup (see MainWindow's constructor). <see cref="Dispose"/> is
/// called from App.xaml.cs's Exit handler so the icon never lingers in
/// the tray after the process has actually exited.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    public static TrayIconService Instance { get; } = new();

    /// <summary>Raised when "Open Solvent" is chosen or the icon is double-clicked.</summary>
    public event Action? OpenRequested;

    /// <summary>Raised when "Run All Maintenance" is chosen from the tray menu.</summary>
    public event Action? RunAllRequested;

    /// <summary>Raised when "Exit" is chosen — the only way to actually quit while running in the background.</summary>
    public event Action? ExitRequested;

    private NotifyIcon? _notifyIcon;

    private TrayIconService() { }

    /// <summary>Creates (if needed) and shows the tray icon.</summary>
    public void Show()
    {
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = true;
            return;
        }

        var loc = LocalizationService.Instance;
        var menu = new ContextMenuStrip();
        menu.Items.Add(loc.Get("Tray_Open"), null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add(loc.Get("Tray_RunAll"), null, (_, _) => RunAllRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(loc.Get("Tray_Exit"), null, (_, _) => ExitRequested?.Invoke());

        _notifyIcon = new NotifyIcon
        {
            Icon = BuildIcon(),
            Text = "Solvent",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    /// <summary>Hides the tray icon (doesn't dispose it — cheap to show again).</summary>
    public void Hide()
    {
        if (_notifyIcon != null)
            _notifyIcon.Visible = false;
    }

    /// <summary>
    /// Small balloon/toast from the tray icon — e.g. "still running in the
    /// background" the first time the window is closed, or "Run All"
    /// finishing while the window is hidden. No-ops silently if the icon
    /// isn't currently shown.
    /// </summary>
    public void Notify(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        if (_notifyIcon is not { Visible: true }) return;
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = icon;
        _notifyIcon.ShowBalloonTip(4000);
    }

    /// <summary>
    /// Draws a small filled circle in Solvent's accent green with a
    /// checkmark, procedurally. The project ships with no bundled .ico
    /// (see README's "Notes" section), so this gives the tray something
    /// on-brand to show without adding an asset dependency; swap in
    /// Assets/solvent.ico here instead if one gets added later.
    /// </summary>
    private static Icon BuildIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var fill = new SolidBrush(ColorTranslator.FromHtml("#22C55E"));
            g.FillEllipse(fill, 1, 1, 30, 30);

            using var pen = new Pen(Color.White, 2.75f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            g.DrawLines(pen, new[] { new PointF(9, 16.5f), new PointF(14, 22), new PointF(23, 10) });
        }

        // NotifyIcon only needs the handle for the life of the process;
        // it's freed automatically when Windows tears down the process,
        // which is the only time this handle needs to go away.
        return Icon.FromHandle(bitmap.GetHicon());
    }

    public void Dispose()
    {
        if (_notifyIcon is null) return;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _notifyIcon = null;
    }
}
