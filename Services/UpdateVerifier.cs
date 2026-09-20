using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Security.Cryptography;

namespace SolventUI.Services;

/// <summary>
/// The trust checks behind self-update. Kept free of WPF and HTTP so the
/// rules are small enough to read in one sitting (and to unit-test).
///
/// Solvent runs elevated and replaces its own exe, so an update that is
/// not verified is a way to run arbitrary code as administrator. Every
/// release therefore ships a <c>Solvent.exe.sha256</c> next to the exe
/// (see .github/workflows/release.yml) and the updater refuses to apply
/// anything whose SHA-256 does not match it.
///
/// Note what this does and does not buy: it catches corrupted, truncated
/// or tampered downloads (CDN, proxy, man-in-the-middle, a swapped file
/// on disk). It does not help if the GitHub account itself is compromised,
/// because the attacker could replace both files. Authenticode-signing the
/// exe and verifying the signature here is the next step for that.
/// </summary>
internal static class UpdateVerifier
{
    /// <summary>Name of the release asset that carries the exe's SHA-256.</summary>
    public const string ChecksumAssetName = "Solvent.exe.sha256";

    /// <summary>
    /// True only for <c>https://github.com/{owner}/{repo}/releases/download/…</c>.
    /// Release asset URLs come from the GitHub API response, so they are
    /// pinned to this repo instead of being trusted blindly. (Defence in
    /// depth — the hash check is what actually protects the exe.)
    /// </summary>
    public static bool IsTrustedReleaseUrl([NotNullWhen(true)] string? url, string owner, string repo)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        return uri.Scheme == Uri.UriSchemeHttps
            && uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.StartsWith(
                $"/{owner}/{repo}/releases/download/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the expected SHA-256 out of a checksum file. Accepts the
    /// <c>sha256sum</c> format (<c>&lt;hash&gt;  Solvent.exe</c> or
    /// <c>&lt;hash&gt; *Solvent.exe</c>) as well as a bare hash. If a file
    /// name is present it must match <paramref name="fileName"/>, so a
    /// checksum meant for some other file is never accepted.
    /// </summary>
    public static bool TryParseChecksum(string? text, string fileName, [NotNullWhen(true)] out string? hash)
    {
        hash = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var firstLine = text.TrimStart('\uFEFF')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (firstLine is null)
            return false;

        var parts = firstLine.Split((char[]?)null, 2,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        var candidate = parts[0];
        if (candidate.Length != 64 || !candidate.All(Uri.IsHexDigit))
            return false;

        if (parts.Length == 2)
        {
            var name = parts[1].TrimStart('*');
            if (!string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        hash = candidate.ToLowerInvariant();
        return true;
    }

    /// <summary>Lower-case hex SHA-256 of a file, streamed (the exe is large).</summary>
    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
