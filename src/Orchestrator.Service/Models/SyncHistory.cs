using System.Text.Json.Serialization;

namespace Orchestrator.Service.Models;

/// <summary>Record of a single sync cycle, appended to sync-history.json.</summary>
public sealed class SyncRecord
{
    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = DateTimeOffset.UtcNow.ToString("O");

    [JsonPropertyName("manifestVersion")]
    public string? ManifestVersion { get; set; }

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("installed")]
    public List<string> Installed { get; set; } = new();

    [JsonPropertyName("updated")]
    public List<string> Updated { get; set; } = new();

    [JsonPropertyName("deleted")]
    public List<string> Deleted { get; set; } = new();

    [JsonPropertyName("errors")]
    public List<string> Errors { get; set; } = new();

    [JsonPropertyName("durationSeconds")]
    public double DurationSeconds { get; set; }
}

/// <summary>Wrapper persisted to sync-history.json (bounded ring of recent records).</summary>
public sealed class SyncHistory
{
    [JsonPropertyName("records")]
    public List<SyncRecord> Records { get; set; } = new();
}
