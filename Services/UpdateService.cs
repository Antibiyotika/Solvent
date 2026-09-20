using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using SolventUI.Models;

namespace SolventUI.Services;

/// <summary>
/// Checks GitHub Releases for a newer build of Solvent and, if the user
/// agrees, downloads it and swaps it in for the currently running exe.
///
/// There is no update server here — "latest release" on the GitHub repo
/// is the source of truth. A release only counts as an update if its tag
/// (e.g. "v1.2.0") parses to a <see cref="System.Version"/> higher than
/// the one baked into this build (SolventUI.csproj's &lt;Version&gt;), and
/// only if it has assets literally named "Solvent.exe" and
/// "Solvent.exe.sha256" attached. The exe is applied only after its
/// SHA-256 matches that checksum file — see <see cref="UpdateVerifier"/>.
/// </summary>
public static class UpdateService
{
    private const string RepoOwner = "Antibiyotika";
    private const string RepoName = "Solvent";
    private const string AssetName = "Solvent.exe";

    // Sanity caps so a bad response can't fill the disk or memory.
    private const long MaxExeBytes = 500L * 1024 * 1024;
    private const int MaxChecksumBytes = 4096;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // GitHub's API rejects requests with no User-Agent outright.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Solvent-App", GetCurrentVersion().ToString()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>The version this running build reports itself as (from the assembly's Version attribute).</summary>
    public static Version GetCurrentVersion() =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    /// <summary>
    /// Queries the repo's latest release. Returns null if there is no
    /// newer version, the asset is missing, or the check fails for any
    /// reason (offline, rate-limited, etc.) — a failed check is silent by
    /// design and never blocks startup or shows an error to the user.
    /// </summary>
    public static async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
            using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogService.Instance.Warning($"Update check failed: GitHub returned {(int)response.StatusCode}.");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var release = JsonSerializer.Deserialize<GitHubRelease>(json, JsonOptions);
            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
                return null;

            var tag = release.TagName.TrimStart('v', 'V');
            if (!Version.TryParse(tag, out var remoteVersion))
                return null;

            var current = GetCurrentVersion();
            if (remoteVersion <= current)
                return null; // already up to date (or somehow ahead)

            var asset = FindAsset(release, AssetName);
            var checksumAsset = FindAsset(release, UpdateVerifier.ChecksumAssetName);
            if (asset is null || checksumAsset is null)
            {
                LogService.Instance.Warning(
                    $"Update {release.TagName} found but is missing {AssetName} or {UpdateVerifier.ChecksumAssetName}; ignoring it.");
                return null;
            }

            if (!UpdateVerifier.IsTrustedReleaseUrl(asset.BrowserDownloadUrl, RepoOwner, RepoName) ||
                !UpdateVerifier.IsTrustedReleaseUrl(checksumAsset.BrowserDownloadUrl, RepoOwner, RepoName))
            {
                LogService.Instance.Warning($"Update {release.TagName} has an untrusted download URL; ignoring it.");
                return null;
            }

            return new UpdateInfo
            {
                Version = remoteVersion,
                TagName = release.TagName,
                DownloadUrl = asset.BrowserDownloadUrl,
                ChecksumUrl = checksumAsset.BrowserDownloadUrl,
                ReleaseNotes = release.Body ?? string.Empty,
                ReleaseUrl = release.HtmlUrl ?? string.Empty,
            };
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning($"Update check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Downloads the new exe, verifies its SHA-256 against the release's
    /// checksum file, and only then hands off to a tiny helper .cmd script
    /// that waits for this process to exit, replaces the old exe with the
    /// new one, relaunches it, and deletes itself. Windows won't let a
    /// running exe overwrite its own file, so this indirection through a
    /// second process is required — there is no in-process way to do it.
    /// Returns false (and leaves everything as it was) if the checksum is
    /// missing or does not match. Call this right before shutting the app
    /// down; it does not return control to a usable app state on success.
    ///
    /// The download and the script are staged next to the running exe, not
    /// in %TEMP%. Solvent runs elevated, and %TEMP% is writable by any
    /// process of the same user — one could swap the verified file (or the
    /// script, which also runs elevated) after the hash check. Whoever can
    /// write next to Solvent.exe could already replace Solvent.exe itself,
    /// so staging there adds no new attack surface.
    /// </summary>
    public static async Task<bool> DownloadAndApplyUpdateAsync(UpdateInfo info, CancellationToken ct = default)
    {
        string? stagedExePath = null;
        string? scriptPath = null;
        var handedOff = false;

        try
        {
            var currentExePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(currentExePath))
            {
                LogService.Instance.Error("Could not determine the running executable's path.");
                return false;
            }

            // UpdateInfo is a plain data object, so re-check its URLs here
            // instead of assuming the caller only ever passes what
            // CheckForUpdateAsync produced.
            if (!UpdateVerifier.IsTrustedReleaseUrl(info.DownloadUrl, RepoOwner, RepoName) ||
                !UpdateVerifier.IsTrustedReleaseUrl(info.ChecksumUrl, RepoOwner, RepoName))
            {
                LogService.Instance.Error("Update aborted: the download URL is not a release asset of this repository.");
                return false;
            }

            var expectedHash = await FetchExpectedHashAsync(info.ChecksumUrl, ct).ConfigureAwait(false);
            if (expectedHash is null)
                return false; // already logged

            var exeDir = Path.GetDirectoryName(currentExePath)!;
            stagedExePath = Path.Combine(exeDir, AssetName + ".new");
            scriptPath = Path.Combine(exeDir, "Solvent.update.cmd");

            LogService.Instance.Info($"Downloading Solvent {info.TagName}...");
            using (var response = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > MaxExeBytes)
                {
                    LogService.Instance.Error("Update aborted: the download is unexpectedly large.");
                    return false;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var file = new FileStream(stagedExePath, FileMode.Create, FileAccess.Write, FileShare.None);
                await stream.CopyToAsync(file, ct).ConfigureAwait(false);
            }

            var actualHash = await UpdateVerifier.ComputeSha256Async(stagedExePath, ct).ConfigureAwait(false);
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                LogService.Instance.Error(
                    $"Update aborted: SHA-256 mismatch (expected {expectedHash}, got {actualHash}). The downloaded file was discarded.");
                return false;
            }

            LogService.Instance.Info("Update verified (SHA-256 matches).");

            var pid = Environment.ProcessId;
            var script =
                "@echo off\r\n" +
                "setlocal\r\n" +
                ":wait\r\n" +
                $"tasklist /fi \"PID eq {pid}\" | find \"{pid}\" >nul\r\n" +
                "if not errorlevel 1 (\r\n" +
                "  timeout /t 1 /nobreak >nul\r\n" +
                "  goto wait\r\n" +
                ")\r\n" +
                $"copy /y \"{CmdEscape(stagedExePath)}\" \"{CmdEscape(currentExePath)}\" >nul && del \"{CmdEscape(stagedExePath)}\"\r\n" +
                $"start \"\" \"{CmdEscape(currentExePath)}\"\r\n" +
                "del \"%~f0\"\r\n";
            await File.WriteAllTextAsync(scriptPath, script, ct).ConfigureAwait(false);

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = scriptPath,
                UseShellExecute = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
            };
            System.Diagnostics.Process.Start(psi);
            handedOff = true;
            return true;
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Update download failed: {ex.Message}");
            return false;
        }
        finally
        {
            // Anything that didn't make it to the helper script is leftover.
            if (!handedOff)
            {
                TryDelete(stagedExePath);
                TryDelete(scriptPath);
            }
        }
    }

    /// <summary>
    /// Fetches the release's checksum file and returns the expected exe
    /// SHA-256, or null (after logging why) if it can't be obtained or
    /// parsed. Fail closed: no valid checksum means no update.
    /// </summary>
    private static async Task<string?> FetchExpectedHashAsync(string checksumUrl, CancellationToken ct)
    {
        using var response = await Http.GetAsync(checksumUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            LogService.Instance.Error($"Update aborted: could not fetch the checksum file (HTTP {(int)response.StatusCode}).");
            return null;
        }

        if (response.Content.Headers.ContentLength is > MaxChecksumBytes)
        {
            LogService.Instance.Error("Update aborted: the checksum file is unexpectedly large.");
            return null;
        }

        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (text.Length > MaxChecksumBytes || !UpdateVerifier.TryParseChecksum(text, AssetName, out var hash))
        {
            LogService.Instance.Error("Update aborted: the checksum file is not a valid SHA-256 for Solvent.exe.");
            return null;
        }

        return hash;
    }

    private static GitHubAsset? FindAsset(GitHubRelease release, string name) =>
        release.Assets?.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Inside a quoted .cmd argument the only character that still needs care is %.</summary>
    private static string CmdEscape(string value) => value.Replace("%", "%%");

    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); } catch { /* best effort */ }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}
