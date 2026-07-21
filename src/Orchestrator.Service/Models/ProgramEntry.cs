// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Describes ONE program listed in the manifest — its id, name, version, where to
//   download it from, where to install it, whether to run it at startup, and so on.
//   The whole manifest is basically a list of these. It also has two small helpers:
//   one that cleans up the checksum string, and one that builds the full path to the
//   installed file.
// =====================================================================================

using System.Text.Json.Serialization;   // for the JSON mapping/converter attributes

namespace Orchestrator.Service.Models;   // groups this with the other data models

/// <summary>Program type as declared in the manifest.</summary>
public enum ProgramType   // the kinds of program we know how to launch
{
    Exe,          // a normal Windows executable
    Batch,        // a .bat script (run via cmd.exe)
    PowerShell,   // a .ps1 script (run via powershell.exe)
    Vbs,          // a .vbs script (run via wscript.exe)
    Python        // a .py script (run via pythonw)
}

/// <summary>Lifecycle status of a program in the manifest.</summary>
public enum ProgramStatus   // whether an entry should exist or be removed
{
    Active,    // should be installed/kept
    Deleted    // should be uninstalled everywhere
}

/// <summary>A single program entry inside <see cref="Manifest"/>.</summary>
public sealed class ProgramEntry
{
    [JsonPropertyName("id")]                          // maps JSON "id"
    public string Id { get; set; } = string.Empty;    // stable unique key used for diffing

    [JsonPropertyName("name")]                        // maps JSON "name"
    public string Name { get; set; } = string.Empty;  // friendly name; also used in startup entry names

    [JsonPropertyName("description")]                 // maps JSON "description"
    public string? Description { get; set; }          // optional human-readable note

    [JsonPropertyName("version")]                     // maps JSON "version"
    public string Version { get; set; } = "1.0";      // version string; a change triggers an update

    [JsonPropertyName("status")]                      // maps JSON "status"
    [JsonConverter(typeof(JsonStringEnumConverter))]  // read/write the enum as text ("active"/"deleted")
    public ProgramStatus Status { get; set; } = ProgramStatus.Active;  // active or deleted; defaults to active

    [JsonPropertyName("type")]                        // maps JSON "type"
    [JsonConverter(typeof(JsonStringEnumConverter))]  // read/write the enum as text ("exe", "batch", ...)
    public ProgramType Type { get; set; } = ProgramType.Exe;  // program kind; defaults to exe

    /// <summary>Raw GitHub URL or repo-relative path fetched via the GitHub API.</summary>
    [JsonPropertyName("url")]                         // maps JSON "url"
    public string? Url { get; set; }                  // a raw.githubusercontent.com link (alternative to Path)

    /// <summary>Repo-relative path (alternative to <see cref="Url"/>) used with the contents API.</summary>
    [JsonPropertyName("path")]                        // maps JSON "path"
    public string? Path { get; set; }                 // repo-relative file path (preferred over Url)

    /// <summary>SHA256 checksum, optionally prefixed with "sha256:".</summary>
    [JsonPropertyName("checksum")]                    // maps JSON "checksum"
    public string? Checksum { get; set; }             // expected file fingerprint for verification

    [JsonPropertyName("installPath")]                 // maps JSON "installPath"
    public string InstallPath { get; set; } = string.Empty;  // folder on disk to install into

    [JsonPropertyName("fileName")]                    // maps JSON "fileName"
    public string FileName { get; set; } = string.Empty;     // file name to save as inside InstallPath

    [JsonPropertyName("arguments")]                   // maps JSON "arguments"
    public string? Arguments { get; set; }            // extra command-line arguments when launching

    [JsonPropertyName("runAtStartup")]                // maps JSON "runAtStartup"
    public bool RunAtStartup { get; set; }            // true = register it to launch at startup

    [JsonPropertyName("runAsAdmin")]                  // maps JSON "runAsAdmin"
    public bool RunAsAdmin { get; set; }              // true = start elevated (via Scheduled Task) instead of a Run entry

    [JsonPropertyName("runOnce")]                     // maps JSON "runOnce"
    public bool RunOnce { get; set; }                 // true = run it once right after install

    [JsonPropertyName("deletedDate")]                 // maps JSON "deletedDate"
    public string? DeletedDate { get; set; }          // when it was marked deleted (for deleted entries)

    [JsonPropertyName("reason")]                      // maps JSON "reason"
    public string? Reason { get; set; }               // why it was deleted (for deleted entries)

    [JsonPropertyName("metadata")]                    // maps JSON "metadata"
    public Dictionary<string, string>? Metadata { get; set; }  // arbitrary extra key/value info (optional)

    /// <summary>Normalized checksum without the "sha256:" prefix, upper-invariant. Null if absent.</summary>
    [JsonIgnore]                                       // computed; not stored in JSON
    public string? NormalizedChecksum
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Checksum)) return null;  // no checksum given -> nothing to normalize
            var c = Checksum.Trim();                               // remove surrounding whitespace
            var idx = c.IndexOf(':');                              // find the ':' after any "sha256:" prefix
            if (idx >= 0) c = c[(idx + 1)..];                      // drop everything up to and including the ':'
            return c.Trim().ToUpperInvariant();                    // return just the hash, upper-cased for easy comparison
        }
    }

    /// <summary>Full local path to the primary file (installPath + fileName).</summary>
    [JsonIgnore]                                       // computed; not stored in JSON
    public string FullFilePath => System.IO.Path.Combine(InstallPath, FileName);  // e.g. C:\...\my-app + my-app.exe
}
