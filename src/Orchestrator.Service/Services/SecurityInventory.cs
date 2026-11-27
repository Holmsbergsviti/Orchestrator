// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Read-only inventory of what's on this PC that could be blocking or interfering with the
//   agent: antivirus products Windows Security Center knows about, the Windows Firewall's
//   on/off state per profile, and the full "Programs and Features" list (broader than
//   Security Center — catches firewalls, VPN/proxy clients, parental-control suites and other
//   leftover previous-owner software that never registers as antivirus). Useful for second-hand
//   PCs, where that kind of software is often still installed and gets in the way. Nothing here
//   removes or changes anything. Antivirus/firewall answers are remembered for an hour because
//   they almost never change and the lookups are not free; the installed-programs list is not
//   cached since callers already schedule it on their own (infrequent) cadence.
// =====================================================================================

using System.Diagnostics;          // for Process (netsh)
using System.Management;           // WMI query
using System.Runtime.Versioning;   // [SupportedOSPlatform("windows")]
using Microsoft.Win32;             // registry (installed-programs inventory)

namespace Orchestrator.Service.Services;

[SupportedOSPlatform("windows")]
public static class SecurityInventory
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(1);
    private static readonly object Gate = new();
    private static List<string> _cached = new();
    private static DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    /// <summary>The display names of the antivirus products Windows Security Center knows about
    /// (for example "Windows Defender", "Avast Free Antivirus"), sorted. Empty when there is none or the lookup
    /// is not possible (older Windows, a server edition without Security Center).</summary>
    public static List<string> GetAntivirusNames()
    {
        lock (Gate)
        {
            if (DateTimeOffset.UtcNow - _cachedAt < CacheFor) return new List<string>(_cached);
            var names = new List<string>();
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT displayName FROM AntiVirusProduct");
                foreach (var obj in searcher.Get())
                {
                    var name = obj["displayName"] as string;
                    if (!string.IsNullOrWhiteSpace(name)) names.Add(name.Trim());
                }
            }
            catch { /* no Security Center on this Windows: report none */ }
            _cached = Normalize(names);
            _cachedAt = DateTimeOffset.UtcNow;
            return new List<string>(_cached);
        }
    }

    /// <summary>Sorted, without blanks or duplicates, so the same set always compares equal. Pure, so it is unit-tested.</summary>
    public static List<string> Normalize(IEnumerable<string> names)
        => names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    private static readonly object FirewallGate = new();
    private static List<string> _firewallCached = new();
    private static DateTimeOffset _firewallCachedAt = DateTimeOffset.MinValue;

    /// <summary>Windows Firewall's on/off state for each profile ("Domain: ON", "Private: OFF", ...),
    /// via <c>netsh advfirewall show allprofiles state</c> — this is diagnostic only (nothing here
    /// changes a rule), meant to answer "is the firewall itself even a candidate for what's blocking
    /// this machine's internet access". Empty if netsh isn't available or its output can't be parsed.</summary>
    public static List<string> GetFirewallProfileState()
    {
        lock (FirewallGate)
        {
            if (DateTimeOffset.UtcNow - _firewallCachedAt < CacheFor) return new List<string>(_firewallCached);
            var result = new List<string>();
            try
            {
                using var proc = Process.Start(new ProcessStartInfo("netsh.exe", "advfirewall show allprofiles state")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                });
                if (proc is not null)
                {
                    var output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(5000);
                    result = ParseFirewallState(output);
                }
            }
            catch { /* netsh unavailable or failed: report none */ }
            _firewallCached = result;
            _firewallCachedAt = DateTimeOffset.UtcNow;
            return new List<string>(result);
        }
    }

    /// <summary>Parses "netsh advfirewall show allprofiles state" output into "Profile: ON/OFF" lines.
    /// Pure (just string parsing), so it is unit-tested without needing netsh on the test machine.</summary>
    public static List<string> ParseFirewallState(string netshOutput)
    {
        var result = new List<string>();
        string? currentProfile = null;
        foreach (var raw in (netshOutput ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.EndsWith("Profile Settings:", StringComparison.OrdinalIgnoreCase))
            {
                currentProfile = line[..line.IndexOf("Profile Settings:", StringComparison.OrdinalIgnoreCase)].Trim();
                continue;
            }
            if (currentProfile is null) continue;
            if (!line.StartsWith("State", StringComparison.OrdinalIgnoreCase)) continue;

            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var value = parts.Length > 1 ? parts[^1].ToUpperInvariant() : "UNKNOWN";
            result.Add($"{currentProfile}: {value}");
            currentProfile = null;   // one "State" line per profile block
        }
        return result;
    }

    /// <summary>The full "Programs and Features" list (registry Uninstall keys, 64-bit + 32-bit views,
    /// plus the current user's own), sorted and deduplicated. Deliberately broader than
    /// <see cref="GetAntivirusNames"/>: a firewall, VPN/proxy client or parental-control suite that
    /// blocks outbound connections usually never registers with Security Center, but it does show up
    /// here the same way it would in "Add or remove programs". Read-only.</summary>
    public static List<string> GetInstalledPrograms()
    {
        var names = new List<string>();
        foreach (var (hive, subKey) in new[]
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")
        })
        {
            try
            {
                using var key = hive.OpenSubKey(subKey);
                if (key is null) continue;
                foreach (var sub in key.GetSubKeyNames())
                {
                    try
                    {
                        using var entry = key.OpenSubKey(sub);
                        // SystemComponent=1 marks an OS-internal piece, not something a person installed
                        // (matches what "Programs and Features" itself filters out).
                        if (entry?.GetValue("SystemComponent") is int sc && sc == 1) continue;
                        var name = entry?.GetValue("DisplayName") as string;
                        if (!string.IsNullOrWhiteSpace(name)) names.Add(name.Trim());
                    }
                    catch { /* one odd subkey shouldn't stop the whole scan */ }
                }
            }
            catch { /* this view not present/readable on this machine: skip it */ }
        }
        return Normalize(names);
    }
}
