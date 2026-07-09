using System.Text.Json.Serialization;

namespace Orchestrator.Service.Models;

/// <summary>Root manifest document fetched from GitHub.</summary>
public sealed class Manifest
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0";

    [JsonPropertyName("lastUpdated")]
    public string? LastUpdated { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>"all" or a list of machine ids (reserved for Phase 2 targeting).</summary>
    [JsonPropertyName("targetMachines")]
    public object? TargetMachines { get; set; }

    [JsonPropertyName("programs")]
    public List<ProgramEntry> Programs { get; set; } = new();

    [JsonIgnore]
    public IEnumerable<ProgramEntry> ActivePrograms =>
        Programs.Where(p => p.Status == ProgramStatus.Active);

    [JsonIgnore]
    public IEnumerable<ProgramEntry> DeletedPrograms =>
        Programs.Where(p => p.Status == ProgramStatus.Deleted);
}
