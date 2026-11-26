// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Asks Windows which antivirus programs are registered on this PC (the same list the
//   "Windows Security" screen shows) so the console can show it per machine. It only READS;
//   it never removes or changes anything. Useful for second-hand PCs, where the previous owner's
//   antivirus is often still installed and gets in the way. The answer is remembered for an hour
//   because it almost never changes and the lookup (WMI) is not free.
// =====================================================================================

using System.Management;           // WMI query
using System.Runtime.Versioning;   // [SupportedOSPlatform("windows")]

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
}
