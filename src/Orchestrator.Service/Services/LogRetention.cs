// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Keeps the agent's own log folder (C:\Windows\Orch\logs) from piling up to gigabytes.
//   Log files are one per day (log-YYYYMMDD.txt). This deletes the ones older than a number
//   of days, and then, if what is left is still bigger than a size cap, the oldest ones
//   until it fits. The newest file is never deleted (it may be open for writing), and the
//   other files in the folder (sync-history.json, remote-sessions.json) are never touched.
//   It never throws: a file that is in use or locked is simply left for next time.
// =====================================================================================

using Microsoft.Extensions.Logging;   // logging

namespace Orchestrator.Service.Services;

public static class LogRetention
{
    /// <summary>Delete old/oversized daily log files. Returns how many files were removed.
    /// <paramref name="maxAgeDays"/> or <paramref name="maxTotalBytes"/> of 0 or less switches that rule off.</summary>
    public static int Prune(string logsDir, int maxAgeDays, long maxTotalBytes, DateTime nowUtc, ILogger? log = null)
    {
        var removed = 0;
        try
        {
            if (!Directory.Exists(logsDir)) return 0;

            // Newest first by last write; the first one is the day's live file and is always kept.
            var files = new DirectoryInfo(logsDir).GetFiles("log-*.txt")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
            if (files.Count <= 1) return 0;
            var keep = files.Skip(1).ToList();   // candidates, still newest first

            // 1. Too old.
            if (maxAgeDays > 0)
            {
                var cutoff = nowUtc - TimeSpan.FromDays(maxAgeDays);
                foreach (var f in keep.Where(f => f.LastWriteTimeUtc < cutoff).ToList())
                    if (TryDelete(f, log)) { removed++; keep.Remove(f); }
            }

            // 2. Still over the size cap: drop the oldest until it fits (the live file counts toward the total).
            if (maxTotalBytes > 0)
            {
                var total = files[0].Length + keep.Sum(f => f.Length);
                for (var i = keep.Count - 1; i >= 0 && total > maxTotalBytes; i--)
                {
                    var size = keep[i].Length;
                    if (TryDelete(keep[i], log)) { removed++; total -= size; }
                }
            }
        }
        catch (Exception ex)
        {
            log?.LogDebug(ex, "Log cleanup skipped");   // housekeeping must never hurt the service
        }

        if (removed > 0) log?.LogInformation("Log cleanup: removed {Count} old log file(s)", removed);
        return removed;
    }

    private static bool TryDelete(FileInfo file, ILogger? log)
    {
        try { file.Delete(); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.LogDebug(ex, "Could not delete {File}", file.Name);   // in use right now; next time
            return false;
        }
    }
}
