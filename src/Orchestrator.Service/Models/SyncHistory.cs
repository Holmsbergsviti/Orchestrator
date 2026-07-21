// =====================================================================================
// FILE PURPOSE (in plain terms):
//   The shape of the sync-history.json log file. Every time the service runs a sync,
//   it writes down one "SyncRecord" (when it ran, whether it succeeded, what it
//   installed/updated/deleted, any errors, and how long it took). SyncHistory is
//   just the wrapper that holds the recent list of those records.
// =====================================================================================

using System.Text.Json.Serialization;   // for [JsonPropertyName] JSON mapping

namespace Orchestrator.Service.Models;   // groups this with the other data models

/// <summary>Record of a single sync cycle, appended to sync-history.json.</summary>
public sealed class SyncRecord
{
    [JsonPropertyName("timestamp")]                                    // maps JSON "timestamp"
    public string Timestamp { get; set; } = DateTimeOffset.UtcNow.ToString("O");  // when this sync ran (defaults to now, ISO-8601)

    [JsonPropertyName("manifestVersion")]                              // maps JSON "manifestVersion"
    public string? ManifestVersion { get; set; }                      // version of the manifest that was applied

    [JsonPropertyName("success")]                                     // maps JSON "success"
    public bool Success { get; set; }                                 // true if the cycle finished without errors

    [JsonPropertyName("installed")]                                   // maps JSON "installed"
    public List<string> Installed { get; set; } = new();             // names of programs installed this cycle

    [JsonPropertyName("updated")]                                    // maps JSON "updated"
    public List<string> Updated { get; set; } = new();              // names of programs updated this cycle

    [JsonPropertyName("deleted")]                                   // maps JSON "deleted"
    public List<string> Deleted { get; set; } = new();             // names of programs removed this cycle

    [JsonPropertyName("errors")]                                   // maps JSON "errors"
    public List<string> Errors { get; set; } = new();             // any error messages from this cycle

    [JsonPropertyName("durationSeconds")]                         // maps JSON "durationSeconds"
    public double DurationSeconds { get; set; }                   // how long the cycle took, in seconds
}

/// <summary>Wrapper persisted to sync-history.json (bounded ring of recent records).</summary>
public sealed class SyncHistory
{
    [JsonPropertyName("records")]                                 // maps JSON "records"
    public List<SyncRecord> Records { get; set; } = new();       // the list of recent sync records
}
