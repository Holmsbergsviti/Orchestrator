// =====================================================================================
// FILE PURPOSE (in plain terms):
//   The console's "what have I done" log. Every action you take here (shut down, restart,
//   wake, screenshot, remote session, uninstall, remove, run-now, program edits) is already
//   a commit in the control repo with a message like "console: restart <machine> (<time>)",
//   signed with your key and with GitHub's own timestamp. So the log is simply that history,
//   read back and tidied up: nothing extra is stored, it can't drift from what really
//   happened, and it survives reinstalling or moving the console.
//
//   It records what you ASKED for. What a PC then did with the request (and whether a live
//   session connected) is in that PC's own log.
// =====================================================================================

using System.Text.RegularExpressions;   // reading the commit subjects

namespace Orchestrator.Console;

/// <summary>One line of the log, as the web page receives it.</summary>
public sealed class ActivityEntry
{
    /// <summary>When it happened (the commit's own timestamp, ISO 8601).</summary>
    public string Utc { get; set; } = "";
    /// <summary>shutdown | restart | uninstall | remote | screenshot | wake | run-now | add-program |
    /// delete-program | save | removed.</summary>
    public string Kind { get; set; } = "";
    /// <summary>Machines it was aimed at (ids). Empty for things about programs.</summary>
    public List<string> MachineIds { get; set; } = new();
    /// <summary>A program id, or the machine's name for "removed" (it is no longer in the list to look up).</summary>
    public string? Detail { get; set; }
    public string Commit { get; set; } = "";
}

public static partial class ActivityLog
{
    [GeneratedRegex(@"^console: (?<what>.+?)(?: \(\d{4}-\d\d-\d\d \d\d:\d\d:\d\dZ\))?$")]
    private static partial Regex SubjectRx();
    [GeneratedRegex(@"^(?<k>shutdown|restart|uninstall) (?<id>\S+)$")]
    private static partial Regex PowerRx();
    [GeneratedRegex(@"^(?<k>remote-session|screenshot) (?<id>\S+)$")]
    private static partial Regex MachineRx();
    [GeneratedRegex(@"^wake \d+ machine\(s\)(?:: (?<ids>\S+))?$")]
    private static partial Regex WakeRx();
    [GeneratedRegex(@"^(?<k>run-now|add program|delete program) (?<p>.+)$")]
    private static partial Regex ProgramRx();
    [GeneratedRegex(@"^removed machine (?<host>.+) \((?<id>[^)]+)\)$")]
    private static partial Regex RemovedRx();

    /// <summary>Turn one commit into a log entry, or null when it isn't something the operator did
    /// (the relay-hint and signing housekeeping commits, agent heartbeats, CI's agent.json, ...).</summary>
    public static ActivityEntry? Parse(string commit, string utc, string subject)
    {
        var m = SubjectRx().Match(subject.Trim());
        if (!m.Success) return null;
        var what = m.Groups["what"].Value;

        ActivityEntry Make(string kind, IEnumerable<string>? ids = null, string? detail = null)
            => new() { Utc = utc, Kind = kind, MachineIds = ids?.ToList() ?? new(), Detail = detail, Commit = commit.Length > 7 ? commit[..7] : commit };

        if (PowerRx().Match(what) is { Success: true } p) return Make(p.Groups["k"].Value, new[] { p.Groups["id"].Value });
        if (MachineRx().Match(what) is { Success: true } r)
            return Make(r.Groups["k"].Value == "remote-session" ? "remote" : "screenshot", new[] { r.Groups["id"].Value });
        if (WakeRx().Match(what) is { Success: true } w)
            return Make("wake", w.Groups["ids"].Success ? w.Groups["ids"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries) : null);
        if (ProgramRx().Match(what) is { Success: true } g)
            return Make(g.Groups["k"].Value switch { "run-now" => "run-now", "add program" => "add-program", _ => "delete-program" }, null, g.Groups["p"].Value);
        if (RemovedRx().Match(what) is { Success: true } d) return Make("removed", new[] { d.Groups["id"].Value }, d.Groups["host"].Value);
        if (what == "update programs/targeting/labels") return Make("save");
        return null;   // "relay hint", "sign ...", and anything we don't recognise
    }

    /// <summary>Merge the entries of several branches, newest first, at most <paramref name="limit"/>.</summary>
    public static List<ActivityEntry> Merge(IEnumerable<ActivityEntry> entries, int limit)
        => entries.OrderByDescending(e => DateTimeOffset.TryParse(e.Utc, out var t) ? t : DateTimeOffset.MinValue)
                  .Take(Math.Clamp(limit, 1, 500)).ToList();
}
