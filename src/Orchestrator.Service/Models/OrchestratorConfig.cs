using System.Text.Json.Serialization;

namespace Orchestrator.Service.Models;

/// <summary>
/// Bound from the "Orchestrator" section of appsettings.json.
/// Static configuration set at install time.
/// </summary>
public sealed class OrchestratorConfig
{
    public const string SectionName = "Orchestrator";

    /// <summary>Root install directory. Default C:\Orchestrator.</summary>
    public string RootPath { get; set; } = @"C:\Orchestrator";

    /// <summary>GitHub repository owner (user or org).</summary>
    public string RepoOwner { get; set; } = string.Empty;

    /// <summary>GitHub repository name.</summary>
    public string RepoName { get; set; } = string.Empty;

    /// <summary>Branch to read from.</summary>
    public string Branch { get; set; } = "main";

    /// <summary>Repo-relative path to the manifest.</summary>
    public string ManifestPath { get; set; } = "manifest.json";

    /// <summary>Personal Access Token with repo:read scope. Empty for public repos.</summary>
    public string GitHubToken { get; set; } = string.Empty;

    /// <summary>Minutes between sync cycles.</summary>
    public int SyncIntervalMinutes { get; set; } = 60;

    /// <summary>Registry hive path for startup entries.</summary>
    public string StartupRegistryKey { get; set; } =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Prefix applied to registry entry names to namespace them.</summary>
    public string RegistryEntryPrefix { get; set; } = "Orch_";

    [JsonIgnore] public string ProgramsPath => Path.Combine(RootPath, "programs");
    [JsonIgnore] public string LogsPath => Path.Combine(RootPath, "logs");
    [JsonIgnore] public string CachePath => Path.Combine(RootPath, "cache");
    [JsonIgnore] public string LocalManifestPath => Path.Combine(CachePath, "local-manifest.json");
    [JsonIgnore] public string ChecksumsPath => Path.Combine(CachePath, "checksums.json");
    [JsonIgnore] public string SyncHistoryPath => Path.Combine(LogsPath, "sync-history.json");
    [JsonIgnore] public string MachineConfigPath => Path.Combine(RootPath, "config.json");
}

/// <summary>Per-machine mutable state persisted to config.json (MachineID etc.).</summary>
public sealed class MachineConfig
{
    [JsonPropertyName("machineId")]
    public string MachineId { get; set; } = string.Empty;

    [JsonPropertyName("firstRun")]
    public string FirstRun { get; set; } = string.Empty;

    [JsonPropertyName("hostname")]
    public string Hostname { get; set; } = string.Empty;

    /// <summary>Ids of runOnce programs already executed on this machine.</summary>
    [JsonPropertyName("completedRunOnce")]
    public List<string> CompletedRunOnce { get; set; } = new();
}
