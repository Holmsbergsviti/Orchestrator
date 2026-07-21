// =====================================================================================
// FILE PURPOSE (in plain terms):
//   The C# shape of the whole manifest.json file that lives in your GitHub repo.
//   When the service downloads that JSON, it gets turned into one of these objects
//   so the rest of the code can work with it as normal properties instead of raw
//   text. It mostly holds the list of programs, plus a couple of shortcuts to grab
//   only the "active" ones or only the "deleted" ones.
// =====================================================================================

using System.Text.Json.Serialization;   // gives us [JsonPropertyName]/[JsonIgnore] to map JSON keys to C# properties

namespace Orchestrator.Service.Models;   // groups this class with the other data-model classes

/// <summary>Root manifest document fetched from GitHub.</summary>
public sealed class Manifest
{
    [JsonPropertyName("version")]                       // maps the JSON "version" field
    public string Version { get; set; } = "1.0";        // manifest format/version string, defaults to "1.0"

    [JsonPropertyName("lastUpdated")]                   // maps the JSON "lastUpdated" field
    public string? LastUpdated { get; set; }            // when the manifest was last edited (optional)

    [JsonPropertyName("description")]                   // maps the JSON "description" field
    public string? Description { get; set; }            // human-readable note about this manifest (optional)

    /// <summary>"all" or a list of machine ids (reserved for Phase 2 targeting).</summary>
    [JsonPropertyName("targetMachines")]                // maps the JSON "targetMachines" field
    public object? TargetMachines { get; set; }         // which machines this applies to; not used yet

    [JsonPropertyName("programs")]                      // maps the JSON "programs" array
    public List<ProgramEntry> Programs { get; set; } = new();  // the actual list of programs to manage

    [JsonIgnore]                                        // computed, so don't read/write it to JSON
    public IEnumerable<ProgramEntry> ActivePrograms =>  // shortcut: just the programs marked "active"
        Programs.Where(p => p.Status == ProgramStatus.Active);

    [JsonIgnore]                                        // computed, so don't read/write it to JSON
    public IEnumerable<ProgramEntry> DeletedPrograms => // shortcut: just the programs marked "deleted"
        Programs.Where(p => p.Status == ProgramStatus.Deleted);
}
