// =====================================================================================
// FILE PURPOSE (in plain terms):
//   What one machine reports back to GitHub about itself: who it is, when it was last
//   seen, and the outcome of its most recent sync (what it's running, and whether the
//   last cycle succeeded). The service commits this as state/<machineId>.json on the
//   fleet-state branch so an operator can see the whole fleet. The Signature property
//   is everything EXCEPT the timestamp, so we can tell a real change from just "still
//   alive" and avoid committing on every single cycle.
// =====================================================================================

using System.Text.Json.Serialization;   // for [JsonPropertyName]/[JsonIgnore] JSON mapping

namespace Orchestrator.Service.Models;   // groups this with the other data models

/// <summary>One machine's self-report, committed to <c>state/&lt;machineId&gt;.json</c>.</summary>
public sealed class Heartbeat
{
    [JsonPropertyName("machineId")]
    public string MachineId { get; set; } = string.Empty;        // this machine's unique id

    [JsonPropertyName("hostname")]
    public string Hostname { get; set; } = string.Empty;         // this machine's name

    [JsonPropertyName("os")]
    public string Os { get; set; } = string.Empty;              // OS description (e.g. "Microsoft Windows 10.0.19045")

    [JsonPropertyName("agentVersion")]
    public string AgentVersion { get; set; } = string.Empty;    // the orchestrator service's own version

    [JsonPropertyName("lastSeenUtc")]
    public string LastSeenUtc { get; set; } = string.Empty;     // when this heartbeat was produced (ISO-8601 UTC)

    [JsonPropertyName("syncIntervalMinutes")]
    public int SyncIntervalMinutes { get; set; }                // how often this machine syncs (helps judge "is it overdue")

    [JsonPropertyName("lastSyncSuccess")]
    public bool LastSyncSuccess { get; set; }                   // did the most recent sync finish without errors?

    [JsonPropertyName("manifestVersion")]
    public string? ManifestVersion { get; set; }               // manifest version applied on the last sync

    [JsonPropertyName("appliedProgramIds")]
    public List<string> AppliedProgramIds { get; set; } = new();  // ids of programs currently active on this machine

    [JsonPropertyName("lastError")]
    public string? LastError { get; set; }                     // first error from the last sync, if any (for quick triage)

    /// <summary>
    /// A stable fingerprint of everything that MATTERS (i.e. not the timestamp). If this
    /// is unchanged from the last pushed heartbeat, the machine's situation hasn't really
    /// changed and we can skip committing except to refresh "last seen" occasionally.
    /// </summary>
    [JsonIgnore]
    public string Signature =>
        string.Join('|',
            MachineId, Hostname, Os, AgentVersion,
            SyncIntervalMinutes.ToString(),
            LastSyncSuccess.ToString(),
            ManifestVersion ?? "",
            string.Join(',', AppliedProgramIds),
            LastError ?? "");
}
