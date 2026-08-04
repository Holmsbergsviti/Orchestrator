// =====================================================================================
// FILE PURPOSE (in plain terms):
//   The shape of remote-sessions.json — the audit trail of who watched and controlled this
//   machine, and for how long. One record per session: which session, which machine, when it
//   started and ended, how it ended, and whether input was actually injected.
//
//   Why its own file rather than sync-history.json, which this otherwise mirrors: that file is
//   written by the SERVICE (Windows session 0), while a remote session runs as a SEPARATE
//   process in the logged-on user's session and is the only one that knows when the session
//   ended. Two processes doing read-modify-write on one JSON file would eventually interleave
//   and corrupt it — and the thing it would corrupt is the sync history, which has nothing to
//   do with remote control. Same mechanism (a bounded ring of JSON records under logs/), its
//   own file.
// =====================================================================================

using System.Text.Json.Serialization;   // for [JsonPropertyName] JSON mapping

namespace Orchestrator.Service.Models;

/// <summary>Record of one remote-control session, appended to remote-sessions.json.</summary>
public sealed class RemoteSessionRecord
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = "";

    [JsonPropertyName("machineId")]
    public string MachineId { get; set; } = "";

    [JsonPropertyName("hostname")]
    public string Hostname { get; set; } = "";

    [JsonPropertyName("startedUtc")]
    public string StartedUtc { get; set; } = "";

    [JsonPropertyName("endedUtc")]
    public string EndedUtc { get; set; } = "";

    [JsonPropertyName("durationSeconds")]
    public double DurationSeconds { get; set; }

    /// <summary>How it ended: "operator", "banner", "timeout", "disconnected", or "failed".</summary>
    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = "";

    /// <summary>How many input events were injected. Zero means it was only ever watched —
    /// worth being able to tell apart afterwards.</summary>
    [JsonPropertyName("inputEvents")]
    public long InputEvents { get; set; }

    /// <summary>How many times the operator extended the session past its first grant.</summary>
    [JsonPropertyName("renewals")]
    public int Renewals { get; set; }
}

/// <summary>Wrapper persisted to remote-sessions.json (bounded ring of recent sessions).</summary>
public sealed class RemoteSessionAudit
{
    /// <summary>Kept deliberately larger than the sync history's: these are rare events, and an
    /// audit trail that quietly discards the session someone is asking about is worthless.</summary>
    public const int MaxRecords = 500;

    [JsonPropertyName("records")]
    public List<RemoteSessionRecord> Records { get; set; } = new();

    /// <summary>Append one record and drop the oldest beyond the limit. Pure, so the ring
    /// behavior is testable without touching the disk.</summary>
    public void Append(RemoteSessionRecord record)
    {
        Records.Add(record);
        if (Records.Count > MaxRecords)
            Records.RemoveRange(0, Records.Count - MaxRecords);
    }
}
