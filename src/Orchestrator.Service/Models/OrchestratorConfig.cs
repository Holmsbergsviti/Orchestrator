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

    /// <summary>Root install directory. Default C:\Orchestrator.</summary>
    public string RootPath { get; set; } = @"C:\Orchestrator";   // base folder everything lives under

    /// <summary>GitHub repository owner (user or org).</summary>
    public string RepoOwner { get; set; } = string.Empty;        // owner of the control repo

    /// <summary>GitHub repository name.</summary>
    public string RepoName { get; set; } = string.Empty;         // name of the control repo

    /// <summary>Branch to read from.</summary>
    public string Branch { get; set; } = "main";                 // which branch to read

    /// <summary>Repo-relative path to the manifest.</summary>
    public string ManifestPath { get; set; } = "manifest.json";  // where the manifest lives in the repo

    /// <summary>Personal Access Token with repo:read scope. Empty for public repos.</summary>
    public string GitHubToken { get; set; } = string.Empty;      // auth token; blank for public repos

    /// <summary>Minutes between sync cycles.</summary>
    public int SyncIntervalMinutes { get; set; } = 60;           // how often to re-check GitHub

    /// <summary>Registry hive path for startup entries.</summary>
    public string StartupRegistryKey { get; set; } =            // registry key used for startup registrations
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Prefix applied to registry entry names to namespace them.</summary>
    public string RegistryEntryPrefix { get; set; } = "Orch_";   // name prefix so our entries are identifiable

    [JsonIgnore] public string ProgramsPath => Path.Combine(RootPath, "programs");                    // <root>\programs — installed program files
    [JsonIgnore] public string LogsPath => Path.Combine(RootPath, "logs");                            // <root>\logs — log files
    [JsonIgnore] public string CachePath => Path.Combine(RootPath, "cache");                          // <root>\cache — remembered state
    [JsonIgnore] public string LocalManifestPath => Path.Combine(CachePath, "local-manifest.json");   // last manifest we applied
    [JsonIgnore] public string ChecksumsPath => Path.Combine(CachePath, "checksums.json");            // known-good file fingerprints
    [JsonIgnore] public string SyncHistoryPath => Path.Combine(LogsPath, "sync-history.json");        // history of past sync runs
    [JsonIgnore] public string MachineConfigPath => Path.Combine(RootPath, "config.json");            // this machine's own state file
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

    /// <summary>Ids of runOnce programs already executed on this machine.</summary>
    [JsonPropertyName("completedRunOnce")]                   // maps JSON "completedRunOnce"
    public List<string> CompletedRunOnce { get; set; } = new();  // remembers which run-once programs already ran here
}
