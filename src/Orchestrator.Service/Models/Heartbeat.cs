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

    /// <summary>SHA-256 of the exe this machine is actually running. The assembly version above
    /// is the same for every CI build, so it can't answer "is this machine on the current
    /// build?" — the hash can, and it's the same value the self-updater compares against.</summary>
    [JsonPropertyName("agentSha256")]
    public string AgentSha256 { get; set; } = string.Empty;

    [JsonPropertyName("lastSeenUtc")]
    public string LastSeenUtc { get; set; } = string.Empty;     // when this heartbeat was produced (ISO-8601 UTC)

    [JsonPropertyName("syncIntervalMinutes")]
    public int SyncIntervalMinutes { get; set; }                // how often this machine syncs (helps judge "is it overdue")

    /// <summary>The longest this machine goes without pushing a heartbeat when nothing changes.
    /// "Last seen" is only refreshed this often, so anything judging "is it overdue" (the
    /// console's online dot, the fleet-watch workflow) must allow for it, not just for
    /// <see cref="SyncIntervalMinutes"/>.</summary>
    [JsonPropertyName("heartbeatMaxIntervalMinutes")]
    public int HeartbeatMaxIntervalMinutes { get; set; }

    [JsonPropertyName("lastSyncSuccess")]
    public bool LastSyncSuccess { get; set; }                   // did the most recent sync finish without errors?

    [JsonPropertyName("manifestVersion")]
    public string? ManifestVersion { get; set; }               // manifest version applied on the last sync

    [JsonPropertyName("appliedProgramIds")]
    public List<string> AppliedProgramIds { get; set; } = new();  // ids of programs currently active on this machine

    [JsonPropertyName("lastError")]
    public string? LastError { get; set; }                     // first error from the last sync, if any (for quick triage)

    [JsonPropertyName("macAddresses")]
    public List<string> MacAddresses { get; set; } = new();    // this machine's NIC MACs (for Wake-on-LAN)

    /// <summary>Public half of this machine's remote-control signing key (base64 SPKI), so the
    /// console can check that whoever connects for this machine's session really is this
    /// machine. Public on purpose — see RelayIdentity. Null when the agent has no key.</summary>
    [JsonPropertyName("relayPublicKey")]
    public string? RelayPublicKey { get; set; }

    /// <summary>Whether the token this machine reads the control repo with could also PUSH to
    /// it (null = couldn't tell). True means a copy taken from this one machine could run code
    /// as SYSTEM on the whole fleet; the console warns about it. The fix is a separate state
    /// repo plus a read-only control-repo token (docs/SETUP.md, "Lock down main").</summary>
    [JsonPropertyName("controlTokenCanWrite")]
    public bool? ControlTokenCanWrite { get; set; }

    /// <summary>
    /// A stable fingerprint of everything that MATTERS (i.e. not the timestamp). If this
    /// is unchanged from the last pushed heartbeat, the machine's situation hasn't really
    /// changed and we can skip committing except to refresh "last seen" occasionally.
    /// </summary>
    [JsonIgnore]
    public string Signature =>
        string.Join('|',
            // AgentSha256 is part of the fingerprint on purpose: a machine that has just
            // updated itself must push a heartbeat promptly, or the console would keep showing
            // it on the old build until something unrelated changed.
            MachineId, Hostname, Os, AgentVersion, AgentSha256,
            SyncIntervalMinutes.ToString(),
            HeartbeatMaxIntervalMinutes.ToString(),
            LastSyncSuccess.ToString(),
            ManifestVersion ?? "",
            string.Join(',', AppliedProgramIds),
            string.Join(',', MacAddresses),
            RelayPublicKey ?? "",   // a new key must reach the console promptly, not hours later
            ControlTokenCanWrite?.ToString() ?? "",   // so switching to a read-only token clears the console's warning promptly
            LastError ?? "");
}
