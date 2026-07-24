// =====================================================================================
// FILE PURPOSE (in plain terms):
//   The C# side of the single source of truth. The repo-root file defaults.json is
//   embedded into this exe when it's built, and this class reads it once at startup so
//   every hardcoded name/path (service name, exe name, install root, registry key,
//   etc.) comes from that one file. Change a value in defaults.json, rebuild, and the
//   whole service picks it up. Each property also has a literal fallback so the code
//   still works if the embedded file is ever missing (e.g. in some test hosts).
// =====================================================================================

using System.Reflection;                // to read the embedded resource stream
using System.Text.Json;                 // to parse the embedded JSON
using System.Text.Json.Serialization;   // for [JsonPropertyName] mapping

namespace Orchestrator.Service;   // top-level service namespace (visible to Models/Services below it)

/// <summary>
/// Loads the embedded <c>defaults.json</c> (the repo's single source of truth for names
/// and paths) exactly once and exposes its values. Falls back to the literals below if
/// the embedded resource can't be read.
/// </summary>
public sealed class OrchestratorDefaults
{
    /// <summary>The one shared instance, loaded from the embedded defaults.json on first use.</summary>
    public static OrchestratorDefaults Instance { get; } = Load();

    [JsonPropertyName("installRoot")]        public string InstallRoot { get; set; } = @"C:\Windows\Orch";               // base install/data folder
    [JsonPropertyName("serviceName")]        public string ServiceName { get; set; } = "GitHubOrchestrator";            // the Windows service's internal name
    [JsonPropertyName("serviceDisplayName")] public string ServiceDisplayName { get; set; } = "GitHub Orchestrator";    // friendly name shown in Services
    [JsonPropertyName("serviceDescription")] public string ServiceDescription { get; set; } = "Syncs and manages programs from a GitHub manifest.";  // service description text
    [JsonPropertyName("exeName")]            public string ExeName { get; set; } = "orchestrator-service.exe";          // the executable file name
    [JsonPropertyName("registryRunKey")]     public string RegistryRunKey { get; set; } = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";  // registry key for startup entries
    [JsonPropertyName("registryEntryPrefix")]public string RegistryEntryPrefix { get; set; } = "Orch_";                // prefix for our startup entry names
    [JsonPropertyName("manifestFileName")]   public string ManifestFileName { get; set; } = "manifest.json";           // manifest file name inside the control repo
    [JsonPropertyName("defaultBranch")]      public string DefaultBranch { get; set; } = "main";                       // default control-repo branch
    [JsonPropertyName("defaultSyncIntervalMinutes")] public int DefaultSyncIntervalMinutes { get; set; } = 60;         // default minutes between syncs
    [JsonPropertyName("fleetStateBranch")]   public string FleetStateBranch { get; set; } = "fleet-state";           // branch heartbeats are committed to
    [JsonPropertyName("heartbeatMaxIntervalMinutes")] public int HeartbeatMaxIntervalMinutes { get; set; } = 360;    // force a heartbeat at least this often
    [JsonPropertyName("codeRepo")]           public string CodeRepo { get; set; } = "Holmsbergsviti/Orchestrator";     // repo hosting the exe + scripts
    [JsonPropertyName("codeRef")]            public string CodeRef { get; set; } = "main";                             // branch of that code repo

    private static OrchestratorDefaults Load()
    {
        try
        {
            var asm = typeof(OrchestratorDefaults).Assembly;                          // this service assembly (holds the embedded file)
            using var stream = asm.GetManifestResourceStream("defaults.json");        // open the embedded defaults.json
            if (stream is null) return new OrchestratorDefaults();                    // not embedded? -> use the literal fallbacks above
            return JsonSerializer.Deserialize<OrchestratorDefaults>(stream)           // parse it into this class...
                   ?? new OrchestratorDefaults();                                     // ...or fall back if it parsed to null
        }
        catch
        {
            return new OrchestratorDefaults();   // anything goes wrong -> safe literal fallbacks
        }
    }
}
