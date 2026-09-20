using System.IO;
using System.Text.Json;
using SolventUI.Models;

namespace SolventUI.Services;

/// <summary>
/// Totals over the recorded cleanups, for the Dashboard's "space freed" cards.
/// <see cref="Last"/> is null until the first cleanup has been recorded.
/// </summary>
public sealed record CleanupSummary(long TotalFreedBytes, int RunCount, CleanupHistoryEntry? Last)
{
    public static CleanupSummary From(IEnumerable<CleanupHistoryEntry> entries)
    {
        long total = 0;
        var count = 0;
        CleanupHistoryEntry? last = null;

        foreach (var entry in entries)
        {
            total += Math.Max(0, entry.FreedBytes);
            count++;
            if (last is null || entry.Timestamp > last.Timestamp)
                last = entry;
        }

        return new CleanupSummary(total, count, last);
    }
}

/// <summary>
/// A small log of finished cleanups (when, how much, which trigger) kept in
/// <c>%AppData%\Solvent\history.json</c> next to settings.json — it is what
/// the Dashboard's "total freed" numbers are computed from.
///
/// Deliberately stateless between calls: every read and every
/// <see cref="Record"/> goes to the file. Scheduled cleanups run in a
/// separate, short-lived Solvent process while the window may be open in
/// another one; caching the list in memory would let the window overwrite
/// the scheduled run's entry the next time it saved. The file is tiny (capped
/// at <see cref="MaxEntries"/>), so re-reading it is cheap.
///
/// History is a convenience, never a requirement: no method here throws.
/// A missing/unwritable/corrupt file just means less (or no) history.
/// </summary>
public sealed class CleanupHistoryService
{
    /// <summary>Cleanups started from the Cleanup page.</summary>
    public const string SourceManual = "manual";

    /// <summary>The one-click "safe defaults" clean used by Run All and Health Check's Fix button.</summary>
    public const string SourceQuick = "quick";

    /// <summary>The Task Scheduler run (<c>Solvent.exe /autoclean</c>).</summary>
    public const string SourceScheduled = "scheduled";

    /// <summary>Oldest entries are dropped past this many; plenty for statistics, small enough to stay a hand-readable file.</summary>
    public const int MaxEntries = 200;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly object _gate = new();

    public CleanupHistoryService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Solvent", "history.json"))
    {
    }

    public CleanupHistoryService(string filePath) => _filePath = filePath;

    /// <summary>Every recorded cleanup, oldest first. Empty if there is no (readable) history yet.</summary>
    public IReadOnlyList<CleanupHistoryEntry> GetAll()
    {
        lock (_gate)
            return Load();
    }

    public CleanupSummary GetSummary() => CleanupSummary.From(GetAll());

    /// <summary>Appends one finished cleanup run to the history.</summary>
    public void Record(CleanupRunResult result, string source)
    {
        try
        {
            lock (_gate)
            {
                var entries = Load();
                entries.Add(new CleanupHistoryEntry(
                    DateTime.Now, Math.Max(0, result.FreedBytes), result.FilesDeleted, result.Errors, source));

                if (entries.Count > MaxEntries)
                    entries.RemoveRange(0, entries.Count - MaxEntries);

                Save(entries);
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"Could not record cleanup history: {ex.Message}");
        }
    }

    private List<CleanupHistoryEntry> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return new List<CleanupHistoryEntry>();

            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<CleanupHistoryEntry>>(json, JsonOptions)
                   ?? new List<CleanupHistoryEntry>();
        }
        catch (JsonException)
        {
            // Unreadable content: keep the bad file for inspection instead
            // of silently overwriting it on the next Record, and start over.
            try { File.Move(_filePath, _filePath + ".corrupt", overwrite: true); } catch { /* best effort */ }
            return new List<CleanupHistoryEntry>();
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"Could not read cleanup history: {ex.Message}");
            return new List<CleanupHistoryEntry>();
        }
    }

    // Write to a temp file and move it over the real one, so a crash or a
    // power cut mid-write can never leave a half-written history.json.
    private void Save(List<CleanupHistoryEntry> entries)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tempPath = _filePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(entries, JsonOptions));
        File.Move(tempPath, _filePath, overwrite: true);
    }
}
