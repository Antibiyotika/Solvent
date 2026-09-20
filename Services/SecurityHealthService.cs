using System.Text.Json;
using SolventUI.Models;

namespace SolventUI.Services;

// ---------------------------------------------------------------------
// Read-only probes for DiagnosticsService.RunHealthCheckAsync. Kept apart
// from MaintenanceServices.cs (which is mostly *actions* — clean this,
// repair that) because everything here only looks, never touches, except
// for the two small opt-in fixes at the bottom (enable firewall, open
// BitLocker settings) which mirror the same "Fix" pattern already used
// for Defender/printer/etc.
//
// Every probe returns null (or an empty list) on failure instead of
// throwing or guessing — a PowerShell module missing on Windows Home, a
// non-elevated prompt, or a disk that doesn't expose S.M.A.R.T. data are
// all treated as "couldn't check", which DiagnosticsService reads as "say
// nothing" rather than as "this is broken".
// ---------------------------------------------------------------------

public static class SecurityHealthService
{
    private static readonly LogService Log = LogService.Instance;

    /// <summary>Null on failure — older Windows, PowerShell's NetSecurity module missing, or access denied. Never reported as "off" in that case.</summary>
    public static async Task<FirewallStatus?> GetFirewallStatusAsync(CancellationToken ct = default)
    {
        try
        {
            var ps = await ProcessRunner.RunPowerShellAsync(
                "Get-NetFirewallProfile | Select-Object Name,Enabled | ConvertTo-Json -Compress", ct);
            if (!ps.Succeeded || string.IsNullOrWhiteSpace(ps.StdOut)) return null;

            var json = ps.StdOut.Trim();
            using var doc = JsonDocument.Parse(json.StartsWith('[') ? json : $"[{json}]");
            var disabled = new List<string>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var name = item.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                var enabled = item.TryGetProperty("Enabled", out var e) &&
                              (e.ValueKind == JsonValueKind.True || (e.ValueKind == JsonValueKind.Number && e.GetInt32() != 0));
                if (!enabled && name.Length > 0) disabled.Add(name);
            }
            return new FirewallStatus(disabled.Count == 0, disabled);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Per-disk S.M.A.R.T.-derived health via Storage's Get-PhysicalDisk. Empty on failure (needs elevation on some systems, or unsupported on virtual/USB disks) — treated as "nothing to report", not "unhealthy".</summary>
    public static async Task<List<DiskHealthInfo>> GetDiskHealthStatusesAsync(CancellationToken ct = default)
    {
        var results = new List<DiskHealthInfo>();
        try
        {
            var ps = await ProcessRunner.RunPowerShellAsync(
                "Get-PhysicalDisk | Select-Object FriendlyName,HealthStatus | ConvertTo-Json -Compress", ct);
            if (!ps.Succeeded || string.IsNullOrWhiteSpace(ps.StdOut)) return results;

            var json = ps.StdOut.Trim();
            using var doc = JsonDocument.Parse(json.StartsWith('[') ? json : $"[{json}]");
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var name = item.TryGetProperty("FriendlyName", out var n) ? n.GetString() ?? "Disk" : "Disk";
                var health = item.TryGetProperty("HealthStatus", out var h) ? h.GetString() ?? "Unknown" : "Unknown";
                results.Add(new DiskHealthInfo(name, health));
            }
        }
        catch { /* Get-PhysicalDisk unavailable/unsupported here — leave empty */ }
        return results;
    }

    /// <summary>Null when BitLocker's module isn't available (common on Windows Home) or the check fails for any other reason — never reported as "off" in that case.</summary>
    public static async Task<bool?> IsSystemDriveEncryptedAsync(CancellationToken ct = default)
    {
        try
        {
            var systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
            var ps = await ProcessRunner.RunPowerShellAsync(
                $"(Get-BitLockerVolume -MountPoint '{systemDrive}' -ErrorAction Stop).ProtectionStatus", ct);
            if (!ps.Succeeded) return null;
            var text = ps.StdOut.Trim();
            if (text.Length == 0) return null;
            return text == "1" || text.Equals("On", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return null;
        }
    }

    public static Task EnableFirewallAsync(CancellationToken ct = default)
    {
        Log.Info("Enabling Windows Firewall on all profiles...");
        return ProcessRunner.RunAsync("netsh.exe", "advfirewall set allprofiles state on", ct);
    }

    public static Task OpenBitLockerSettingsAsync(CancellationToken ct = default)
    {
        Log.Info("Opening BitLocker settings...");
        // Routed through "cmd /c start", same as WindowsUpdateService.OpenUpdateSettingsAsync —
        // launching a shell-associated control panel applet directly from a
        // redirected-output child process is unreliable; "start" hands it to the shell instead.
        return ProcessRunner.RunAsync("cmd.exe", "/c start control.exe /name Microsoft.BitLockerDriveEncryption", ct);
    }
}
