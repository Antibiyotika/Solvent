using System.IO;
using System.Runtime.InteropServices;
using SolventUI.Models;

namespace SolventUI.Services;

// ---------------------------------------------------------------------
// Cleanup, hardened: every category below is measured first (no deletes)
// so CleanupPage can show real sizes and let the user pick what to
// remove, then CleanSelectedAsync re-resolves the same targets and only
// deletes the categories that were actually checked.
//
// This replaces the old "5 hardcoded locations, no numbers, no choice"
// TaskService.CleanTempAndCacheAsync sweep — that method now delegates
// to ScanAsync/CleanSelectedAsync with every Safe category pre-selected,
// so Health Check's "Fix" button and Run All keep working unchanged but
// benefit from the wider coverage and now log real freed-space numbers.
//
// IMPORTANT FIX: the previous browser-cache cleaner deleted the entire
// contents of "...\Mozilla\Firefox\Profiles", which is the *whole*
// Firefox profile folder — bookmarks (places.sqlite), saved logins
// (logins.json/key4.db), cookies, and open tabs all live there, not
// just cache. That would have silently wiped a user's Firefox data on
// every "Clean now" click. This version only ever touches each
// profile's cache2/startupCache subfolders, mirroring exactly what
// Chrome/Edge already did correctly (Cache-only, not the whole profile).
// ---------------------------------------------------------------------

/// <summary>One thing to measure/delete within a category: either a whole file, or every file matching a pattern under a directory (the directory itself is never removed).</summary>
internal sealed record CleanupTarget(string Path, bool IsDirectory, string SearchPattern = "*", bool Recursive = true);

internal sealed record CleanupCategoryDef(string Id, string TitleKey, string DescriptionKey, CleanupRisk Risk, bool DefaultSelected, Func<List<CleanupTarget>> GetTargets);

public static class CleanupScanService
{
    private static readonly LogService Log = LogService.Instance;

    public const string IdTemp = "temp";
    public const string IdWindowsTemp = "win-temp";
    public const string IdPrefetch = "prefetch";
    public const string IdRecycleBin = "recycle-bin";
    public const string IdBrowserCache = "browser-cache";
    public const string IdWindowsUpdateCache = "wu-cache";
    public const string IdDeliveryOptimization = "delivery-opt";
    public const string IdThumbnailCache = "thumbnail-cache";
    public const string IdShaderCache = "shader-cache";
    public const string IdErrorReports = "error-reports";
    public const string IdMemoryDumps = "memory-dumps";

    private static string WinDir => Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    /// <summary>Every non-browser, non-recycle-bin category — plain "files under a directory" or "a single file" targets.</summary>
    private static readonly List<CleanupCategoryDef> SimpleCategories = new()
    {
        new(IdTemp, "Cleanup_CatTempTitle", "Cleanup_CatTempDesc", CleanupRisk.Safe, true, () => new()
        {
            new(Path.GetTempPath(), IsDirectory: true),
        }),
        new(IdWindowsTemp, "Cleanup_CatWinTempTitle", "Cleanup_CatWinTempDesc", CleanupRisk.Safe, true, () => new()
        {
            new(Path.Combine(WinDir, "Temp"), IsDirectory: true),
        }),
        new(IdPrefetch, "Cleanup_CatPrefetchTitle", "Cleanup_CatPrefetchDesc", CleanupRisk.Safe, true, () => new()
        {
            new(Path.Combine(WinDir, "Prefetch"), IsDirectory: true),
        }),
        new(IdWindowsUpdateCache, "Cleanup_CatWuCacheTitle", "Cleanup_CatWuCacheDesc", CleanupRisk.Safe, true, () => new()
        {
            // Only the re-downloadable staging folder — never the sibling
            // DataStore.edb/state that WindowsUpdateService.ResetComponentsAsync
            // handles separately when a full component reset is needed.
            new(Path.Combine(WinDir, "SoftwareDistribution", "Download"), IsDirectory: true),
        }),
        new(IdDeliveryOptimization, "Cleanup_CatDoTitle", "Cleanup_CatDoDesc", CleanupRisk.Safe, true, () => new()
        {
            new(Path.Combine(WinDir, "SoftwareDistribution", "DeliveryOptimization", "Cache"), IsDirectory: true),
        }),
        new(IdThumbnailCache, "Cleanup_CatThumbTitle", "Cleanup_CatThumbDesc", CleanupRisk.Safe, true, () => new()
        {
            new(Path.Combine(LocalAppData, @"Microsoft\Windows\Explorer"), IsDirectory: true, SearchPattern: "thumbcache_*.db", Recursive: false),
            new(Path.Combine(LocalAppData, @"Microsoft\Windows\Explorer"), IsDirectory: true, SearchPattern: "iconcache_*.db", Recursive: false),
        }),
        new(IdShaderCache, "Cleanup_CatShaderTitle", "Cleanup_CatShaderDesc", CleanupRisk.Safe, true, () => new()
        {
            new(Path.Combine(LocalAppData, "D3DSCache"), IsDirectory: true),
            new(Path.Combine(LocalAppData, @"NVIDIA\DXCache"), IsDirectory: true),
            new(Path.Combine(LocalAppData, @"NVIDIA\GLCache"), IsDirectory: true),
            new(Path.Combine(LocalAppData, @"AMD\DxCache"), IsDirectory: true),
            new(Path.Combine(LocalAppData, @"AMD\DxcCache"), IsDirectory: true),
        }),
        new(IdErrorReports, "Cleanup_CatWerTitle", "Cleanup_CatWerDesc", CleanupRisk.Safe, true, () => new()
        {
            new(Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportQueue"), IsDirectory: true),
            new(Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportArchive"), IsDirectory: true),
            new(Path.Combine(LocalAppData, @"Microsoft\Windows\WER\ReportQueue"), IsDirectory: true),
        }),
        new(IdMemoryDumps, "Cleanup_CatDumpsTitle", "Cleanup_CatDumpsDesc", CleanupRisk.Caution, false, () => new()
        {
            new(Path.Combine(WinDir, "Minidump"), IsDirectory: true, SearchPattern: "*.dmp"),
            new(Path.Combine(WinDir, "MEMORY.DMP"), IsDirectory: false),
            new(Path.Combine(WinDir, "LiveKernelReports"), IsDirectory: true, SearchPattern: "*.dmp"),
        }),
    };

    /// <summary>Runs every category's scan and returns real, measured sizes without deleting anything.</summary>
    public static Task<CleanupScanResult> ScanAsync(CancellationToken ct = default) =>
        Task.Run(() =>
        {
            Log.Info("Cleanup: scanning categories...");
            var categories = new List<CleanupCategory>();

            foreach (var def in SimpleCategories)
            {
                ct.ThrowIfCancellationRequested();
                var (bytes, count) = Measure(SafeInvoke(def.GetTargets));
                categories.Add(ToCategory(def, bytes, count));
            }

            ct.ThrowIfCancellationRequested();
            var (rbBytes, rbCount) = MeasureRecycleBin();
            categories.Add(new CleanupCategory
            {
                Id = IdRecycleBin,
                TitleKey = "Cleanup_CatRecycleBinTitle",
                DescriptionKey = "Cleanup_CatRecycleBinDesc",
                Risk = CleanupRisk.Safe,
                IsSelected = true,
                SizeBytes = rbBytes,
                FileCount = rbCount,
            });

            ct.ThrowIfCancellationRequested();
            var (bcBytes, bcCount) = Measure(GetBrowserCacheTargets());
            categories.Add(new CleanupCategory
            {
                Id = IdBrowserCache,
                TitleKey = "Cleanup_CatBrowserTitle",
                DescriptionKey = "Cleanup_CatBrowserDesc",
                Risk = CleanupRisk.Safe,
                IsSelected = true,
                SizeBytes = bcBytes,
                FileCount = bcCount,
            });

            var total = categories.Sum(c => c.SizeBytes);
            Log.Success($"Cleanup scan finished — {SizeFormat.Format(total)} across {categories.Count} categories.");
            return new CleanupScanResult(categories);
        }, ct);

    /// <summary>Deletes only the given category ids, re-resolving fresh targets (files may have changed since the scan).</summary>
    public static Task<CleanupRunResult> CleanSelectedAsync(
        IReadOnlyCollection<string> selectedIds, IProgress<TaskProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            long freed = 0;
            var deleted = 0;
            var errors = 0;
            var total = selectedIds.Count;
            var step = 0;

            foreach (var def in SimpleCategories)
            {
                ct.ThrowIfCancellationRequested();
                if (!selectedIds.Contains(def.Id)) continue;
                step++;
                progress?.Report(TaskProgress.Step(step, total, $"Cleaning {LocalizationService.Instance.Get(def.TitleKey)}..."));
                var (f, d, e) = CleanTargets(SafeInvoke(def.GetTargets));
                freed += f; deleted += d; errors += e;
                Log.Info($"Cleanup: {LocalizationService.Instance.Get(def.TitleKey)} — freed {SizeFormat.Format(f)}.");
            }

            if (selectedIds.Contains(IdRecycleBin))
            {
                step++;
                progress?.Report(TaskProgress.Step(step, total, "Emptying Recycle Bin..."));
                var (rbBytes, _) = MeasureRecycleBin();
                try
                {
                    NativeMethods.SHEmptyRecycleBin(IntPtr.Zero, null,
                        NativeMethods.SHERB_NOCONFIRMATION | NativeMethods.SHERB_NOPROGRESSUI | NativeMethods.SHERB_NOSOUND);
                }
                catch { /* best-effort */ }
                freed += rbBytes;
                Log.Info($"Cleanup: Recycle Bin — freed {SizeFormat.Format(rbBytes)}.");
            }

            if (selectedIds.Contains(IdBrowserCache))
            {
                step++;
                progress?.Report(TaskProgress.Step(step, total, "Clearing browser caches..."));
                var (f, d, e) = CleanTargets(GetBrowserCacheTargets());
                freed += f; deleted += d; errors += e;
                Log.Info($"Cleanup: browser caches — freed {SizeFormat.Format(f)}.");
            }

            progress?.Report(TaskProgress.Step(total, total, "Cleanup finished."));
            Log.Success($"Cleanup finished — {SizeFormat.Format(freed)} freed ({deleted} item(s) removed{(errors > 0 ? $", {errors} skipped" : "")}).");
            return new CleanupRunResult(freed, deleted, errors);
        }, ct);

    /// <summary>Ids of every category that should be pre-checked the first time a scan renders (all Safe categories).</summary>
    public static IReadOnlyCollection<string> SafeDefaultIds =>
        SimpleCategories.Where(d => d.DefaultSelected).Select(d => d.Id)
            .Concat(new[] { IdRecycleBin, IdBrowserCache })
            .ToList();

    // ---------- Browser caches (Chromium family + Firefox, cache-only) ----------

    /// <summary>
    /// Chrome/Edge/Brave: every profile's Cache/Code Cache/GPUCache/Service Worker
    /// storage. Firefox: every profile's cache2/startupCache only — never the
    /// profile root, which also holds bookmarks, saved logins, and cookies.
    /// </summary>
    private static List<CleanupTarget> GetBrowserCacheTargets()
    {
        var targets = new List<CleanupTarget>();

        foreach (var chromiumRoot in new[]
                 {
                     Path.Combine(LocalAppData, @"Google\Chrome\User Data"),
                     Path.Combine(LocalAppData, @"Microsoft\Edge\User Data"),
                     Path.Combine(LocalAppData, @"BraveSoftware\Brave-Browser\User Data"),
                 })
        {
            if (!Directory.Exists(chromiumRoot)) continue;
            foreach (var profileDir in EnumerateChromiumProfiles(chromiumRoot))
            {
                targets.Add(new CleanupTarget(Path.Combine(profileDir, "Cache"), IsDirectory: true));
                targets.Add(new CleanupTarget(Path.Combine(profileDir, "Code Cache"), IsDirectory: true));
                targets.Add(new CleanupTarget(Path.Combine(profileDir, "GPUCache"), IsDirectory: true));
                targets.Add(new CleanupTarget(Path.Combine(profileDir, @"Service Worker\CacheStorage"), IsDirectory: true));
            }
        }

        var firefoxRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Mozilla\Firefox\Profiles");
        if (Directory.Exists(firefoxRoot))
        {
            foreach (var profileDir in SafeGetDirectories(firefoxRoot))
            {
                // cache2/startupCache only — the rest of the profile (places.sqlite,
                // logins.json, key4.db, cookies.sqlite, ...) is never touched.
                targets.Add(new CleanupTarget(Path.Combine(profileDir, "cache2"), IsDirectory: true));
                targets.Add(new CleanupTarget(Path.Combine(profileDir, "startupCache"), IsDirectory: true));
            }
        }
        // Firefox's cache2 sometimes lives under the *local* (non-roaming) profile
        // mirror instead, depending on install — check both without duplicating
        // work if they resolve to the same folders.
        var firefoxLocalRoot = Path.Combine(LocalAppData, @"Mozilla\Firefox\Profiles");
        if (Directory.Exists(firefoxLocalRoot))
        {
            foreach (var profileDir in SafeGetDirectories(firefoxLocalRoot))
            {
                targets.Add(new CleanupTarget(Path.Combine(profileDir, "cache2"), IsDirectory: true));
                targets.Add(new CleanupTarget(Path.Combine(profileDir, "startupCache"), IsDirectory: true));
            }
        }

        return targets;
    }

    private static IEnumerable<string> EnumerateChromiumProfiles(string userDataDir)
    {
        var defaultDir = Path.Combine(userDataDir, "Default");
        if (Directory.Exists(defaultDir)) yield return defaultDir;

        foreach (var dir in SafeGetDirectories(userDataDir))
        {
            if (Path.GetFileName(dir).StartsWith("Profile ", StringComparison.OrdinalIgnoreCase))
                yield return dir;
        }
    }

    // ---------- Recycle Bin (measured via the shell API, not by walking $Recycle.Bin) ----------

    private static (long Bytes, int Count) MeasureRecycleBin()
    {
        try
        {
            var info = new NativeMethods.SHQUERYRBINFO { cbSize = Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>() };
            var hr = NativeMethods.SHQueryRecycleBin(null, ref info);
            return hr == 0 ? (info.i64Size, (int)Math.Min(int.MaxValue, info.i64NumItems)) : (0, 0);
        }
        catch
        {
            return (0, 0);
        }
    }

    // ---------- Generic measure/clean over CleanupTarget lists ----------

    private static (long Bytes, int Count) Measure(List<CleanupTarget> targets)
    {
        long bytes = 0;
        var count = 0;
        foreach (var target in targets)
        {
            foreach (var file in EnumerateFiles(target))
            {
                try
                {
                    bytes += new FileInfo(file).Length;
                    count++;
                }
                catch { /* vanished mid-scan — skip */ }
            }
        }
        return (bytes, count);
    }

    private static (long Freed, int Deleted, int Errors) CleanTargets(List<CleanupTarget> targets)
    {
        long freed = 0;
        var deleted = 0;
        var errors = 0;
        foreach (var target in targets)
        {
            foreach (var file in EnumerateFiles(target))
            {
                try
                {
                    var size = new FileInfo(file).Length;
                    File.Delete(file);
                    freed += size;
                    deleted++;
                }
                catch
                {
                    errors++; // locked/in-use — normal for a live cache, just skip it
                }
            }
        }
        return (freed, deleted, errors);
    }

    /// <summary>
    /// Materializes the matching files up front (a plain List, not a lazy
    /// iterator) — a yield-returning iterator can't have its Directory.
    /// EnumerateFiles call wrapped in try/catch (CS1626), and access-denied /
    /// path-too-long here is routine, not exceptional.
    /// </summary>
    private static List<string> EnumerateFiles(CleanupTarget target)
    {
        if (!target.IsDirectory)
            return File.Exists(target.Path) ? new List<string> { target.Path } : new List<string>();

        if (!Directory.Exists(target.Path)) return new List<string>();

        try
        {
            return Directory.EnumerateFiles(
                target.Path, target.SearchPattern,
                target.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).ToList();
        }
        catch
        {
            return new List<string>(); // access denied / path too long — skip this target
        }
    }

    private static List<CleanupTarget> SafeInvoke(Func<List<CleanupTarget>> get)
    {
        try { return get(); } catch { return new List<CleanupTarget>(); }
    }

    private static string[] SafeGetDirectories(string dir)
    {
        try { return Directory.GetDirectories(dir); } catch { return Array.Empty<string>(); }
    }

    private static CleanupCategory ToCategory(CleanupCategoryDef def, long bytes, int count) => new()
    {
        Id = def.Id,
        TitleKey = def.TitleKey,
        DescriptionKey = def.DescriptionKey,
        Risk = def.Risk,
        IsSelected = def.DefaultSelected,
        SizeBytes = bytes,
        FileCount = count,
    };
}
