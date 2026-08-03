// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Two settings objects. OrchestratorConfig is the "how am I configured" data that
//   comes from appsettings.json (which repo to watch, how often, where to install) —
//   it's fixed at install time. MachineConfig is this-computer-only state that the
//   service writes itself (a unique machine ID, first-run time, which run-once
//   programs already ran here). The many "=> Path.Combine(...)" lines are just
//   convenient shortcuts to the standard sub-folders/files under the install root.
// =====================================================================================

using System.Text.Json.Serialization;   // for [JsonPropertyName]/[JsonIgnore] JSON mapping

namespace Orchestrator.Service.Models;   // groups this with the other data models

/// <summary>
/// Bound from the "Orchestrator" section of appsettings.json.
/// Static configuration set at install time.
/// </summary>
public sealed class OrchestratorConfig
{
    public const string SectionName = "Orchestrator";   // the appsettings.json section this maps to

    /// <summary>Root install directory. Default comes from defaults.json (C:\Windows\Orch).</summary>
    public string RootPath { get; set; } = OrchestratorDefaults.Instance.InstallRoot;   // base folder everything lives under (from defaults.json)

    /// <summary>GitHub repository owner (user or org).</summary>
    public string RepoOwner { get; set; } = string.Empty;        // owner of the control repo

    /// <summary>GitHub repository name.</summary>
    public string RepoName { get; set; } = string.Empty;         // name of the control repo

    /// <summary>Branch to read from.</summary>
    public string Branch { get; set; } = OrchestratorDefaults.Instance.DefaultBranch;    // which branch to read (from defaults.json)

    /// <summary>Repo-relative path to the manifest.</summary>
    public string ManifestPath { get; set; } = OrchestratorDefaults.Instance.ManifestFileName;  // where the manifest lives in the repo (from defaults.json)

    /// <summary>Personal Access Token with repo:read scope. Empty for public repos.</summary>
    public string GitHubToken { get; set; } = string.Empty;      // auth token; blank for public repos

    /// <summary>Minutes between sync cycles.</summary>
    public int SyncIntervalMinutes { get; set; } = OrchestratorDefaults.Instance.DefaultSyncIntervalMinutes;  // how often to re-check GitHub (from defaults.json)

    /// <summary>Registry hive path for startup entries.</summary>
    public string StartupRegistryKey { get; set; } =            // registry key used for startup registrations (from defaults.json)
        OrchestratorDefaults.Instance.RegistryRunKey;

    /// <summary>Prefix applied to registry entry names to namespace them.</summary>
    public string RegistryEntryPrefix { get; set; } = OrchestratorDefaults.Instance.RegistryEntryPrefix;   // name prefix so our entries are identifiable (from defaults.json)

    /// <summary>Whether this machine reports a heartbeat back to GitHub. Needs a token with write access.</summary>
    public bool ReportState { get; set; } = true;               // true = commit state/<machineId>.json each time it changes

    /// <summary>Branch the heartbeat files are committed to (kept off the main branch).</summary>
    public string FleetStateBranch { get; set; } = OrchestratorDefaults.Instance.FleetStateBranch;   // where heartbeats live (from defaults.json)

    /// <summary>Push a heartbeat at least this often even when nothing changed (freshness bound).</summary>
    public int HeartbeatMaxIntervalMinutes { get; set; } = OrchestratorDefaults.Instance.HeartbeatMaxIntervalMinutes;  // caps how stale "last seen" can get (from defaults.json)

    /// <summary>If true, this (always-on) machine sends Wake-on-LAN magic packets for wake requests.</summary>
    public bool IsWaker { get; set; } = false;   // set true on one always-on machine per network segment

    /// <summary>wss:// address of the console's relay (only needed if you use live remote-control
    /// sessions). Blank = the feature is unavailable on this machine.</summary>
    public string RelayUrl { get; set; } = string.Empty;

    /// <summary>SHA-1 thumbprint of the console's HTTPS certificate, as shown by the console at
    /// startup (spaces/colons are ignored). Set this when the console uses a self-signed
    /// certificate: the agent then accepts exactly that one certificate and nothing else —
    /// which is stricter than normal CA trust, not weaker. Blank = require normal chain trust.</summary>
    public string RelayCertThumbprint { get; set; } = string.Empty;

    /// <summary>Hard cap on how long a single remote-control session may run before it's force-ended.</summary>
    public int RemoteSessionMaxMinutes { get; set; } = 30;

    [JsonIgnore] public string ProgramsPath => Path.Combine(RootPath, "programs");                    // <root>\programs — installed program files
    [JsonIgnore] public string LogsPath => Path.Combine(RootPath, "logs");                            // <root>\logs — log files
    [JsonIgnore] public string CachePath => Path.Combine(RootPath, "cache");                          // <root>\cache — remembered state
    [JsonIgnore] public string LocalManifestPath => Path.Combine(CachePath, "local-manifest.json");   // last manifest we applied
    [JsonIgnore] public string ChecksumsPath => Path.Combine(CachePath, "checksums.json");            // known-good file fingerprints
    [JsonIgnore] public string SyncHistoryPath => Path.Combine(LogsPath, "sync-history.json");        // history of past sync runs
    [JsonIgnore] public string MachineConfigPath => Path.Combine(RootPath, "config.json");            // this machine's own state file
    [JsonIgnore] public string LastHeartbeatPath => Path.Combine(CachePath, "last-heartbeat.json");   // the last heartbeat we pushed (for change detection)
}

/// <summary>Per-machine mutable state persisted to config.json (MachineID etc.).</summary>
public sealed class MachineConfig
{
    [JsonPropertyName("machineId")]                          // maps JSON "machineId"
    public string MachineId { get; set; } = string.Empty;    // a unique ID generated for this computer

    [JsonPropertyName("firstRun")]                           // maps JSON "firstRun"
    public string FirstRun { get; set; } = string.Empty;     // timestamp of the very first run on this computer

    [JsonPropertyName("hostname")]                           // maps JSON "hostname"
    public string Hostname { get; set; } = string.Empty;     // this computer's name

    /// <summary>Ids of runOnceInstalled programs already executed on this machine.</summary>
    [JsonPropertyName("completedRunOnce")]                   // maps JSON "completedRunOnce"
    public List<string> CompletedRunOnce { get; set; } = new();  // remembers which run-once-on-install programs already ran here

    /// <summary>Program id -> the last "runRequest" token already executed here (so each token runs once).</summary>
    [JsonPropertyName("completedRunRequests")]               // maps JSON "completedRunRequests"
    public Dictionary<string, string> CompletedRunRequests { get; set; } = new();  // remembers the last run-now token run per program

    /// <summary>Ids of admin commands (shutdown/restart) already executed here, so they never re-run.</summary>
    [JsonPropertyName("completedCommands")]                  // maps JSON "completedCommands"
    public List<string> CompletedCommands { get; set; } = new();  // remembers which command nonces already ran here

    /// <summary>Ids of Wake-on-LAN requests this waker has already sent, so it won't re-send them.</summary>
    [JsonPropertyName("completedWakes")]                     // maps JSON "completedWakes"
    public List<string> CompletedWakes { get; set; } = new();     // remembers which wake nonces this waker sent

    /// <summary>Ids of screenshot requests already scheduled here, so a request only captures once.</summary>
    [JsonPropertyName("completedScreenshots")]               // maps JSON "completedScreenshots"
    public List<string> CompletedScreenshots { get; set; } = new();  // remembers which screenshot nonces already ran here

    /// <summary>Ids of remote-control session requests already scheduled here, so a request only starts once.</summary>
    [JsonPropertyName("completedRemoteSessions")]             // maps JSON "completedRemoteSessions"
    public List<string> CompletedRemoteSessions { get; set; } = new();  // remembers which session nonces already ran here
}
