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
/// only if it has an asset literally named "Solvent.exe" attached.
/// </summary>
public static class UpdateService
{
    private const string RepoOwner = "Antibiyotika";
    private const string RepoName = "Solvent";
    private const string AssetName = "Solvent.exe";

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

            var asset = release.Assets?.FirstOrDefault(
                a => string.Equals(a.Name, AssetName, StringComparison.OrdinalIgnoreCase));
            if (asset is null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
            {
                LogService.Instance.Warning($"Update {release.TagName} found but has no {AssetName} asset attached.");
                return null;
            }

            return new UpdateInfo
            {
                Version = remoteVersion,
                TagName = release.TagName,
                DownloadUrl = asset.BrowserDownloadUrl,
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
    /// Downloads the new exe, then hands off to a tiny helper .cmd script
    /// that waits for this process to exit, replaces the old exe with the
    /// new one, relaunches it, and deletes itself. Windows won't let a
    /// running exe overwrite its own file, so this indirection through a
    /// second process is required — there is no in-process way to do it.
    /// Call this right before shutting the app down; it does not return
    /// control to a usable app state on success.
    /// </summary>
    public static async Task<bool> DownloadAndApplyUpdateAsync(UpdateInfo info, CancellationToken ct = default)
    {
        try
        {
            var currentExePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(currentExePath))
            {
                LogService.Instance.Error("Could not determine the running executable's path.");
                return false;
            }

            var updateDir = Path.Combine(Path.GetTempPath(), "Solvent_update");
            Directory.CreateDirectory(updateDir);
            var newExePath = Path.Combine(updateDir, AssetName);

            LogService.Instance.Info($"Downloading Solvent {info.TagName}...");
            await using (var stream = await Http.GetStreamAsync(info.DownloadUrl, ct).ConfigureAwait(false))
            await using (var file = File.Create(newExePath))
            {
                await stream.CopyToAsync(file, ct).ConfigureAwait(false);
            }

            var scriptPath = Path.Combine(updateDir, "apply_update.cmd");
            var script =
                "@echo off\r\n" +
                "setlocal\r\n" +
                ":wait\r\n" +
                $"tasklist /fi \"PID eq {Environment.ProcessId}\" | find \"{Environment.ProcessId}\" >nul\r\n" +
                "if not errorlevel 1 (\r\n" +
                "  timeout /t 1 /nobreak >nul\r\n" +
                "  goto wait\r\n" +
                ")\r\n" +
                $"copy /y \"{newExePath}\" \"{currentExePath}\" >nul\r\n" +
                $"start \"\" \"{currentExePath}\"\r\n" +
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
            return true;
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"Update download failed: {ex.Message}");
            return false;
        }
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
