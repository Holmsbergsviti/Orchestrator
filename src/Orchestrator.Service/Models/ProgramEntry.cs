using System.Text.Json.Serialization;

namespace Orchestrator.Service.Models;

/// <summary>Program type as declared in the manifest.</summary>
public enum ProgramType
{
    Exe,
    Batch,
    PowerShell,
    Vbs,
    Python
}

/// <summary>Lifecycle status of a program in the manifest.</summary>
public enum ProgramStatus
{
    Active,
    Deleted
}

/// <summary>A single program entry inside <see cref="Manifest"/>.</summary>
public sealed class ProgramEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0";

    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ProgramStatus Status { get; set; } = ProgramStatus.Active;

    [JsonPropertyName("type")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ProgramType Type { get; set; } = ProgramType.Exe;

    /// <summary>Raw GitHub URL or repo-relative path fetched via the GitHub API.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>Repo-relative path (alternative to <see cref="Url"/>) used with the contents API.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    /// <summary>SHA256 checksum, optionally prefixed with "sha256:".</summary>
    [JsonPropertyName("checksum")]
    public string? Checksum { get; set; }

    [JsonPropertyName("installPath")]
    public string InstallPath { get; set; } = string.Empty;

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public string? Arguments { get; set; }

    [JsonPropertyName("runAtStartup")]
    public bool RunAtStartup { get; set; }

    [JsonPropertyName("runAsAdmin")]
    public bool RunAsAdmin { get; set; }

    [JsonPropertyName("runOnce")]
    public bool RunOnce { get; set; }

    [JsonPropertyName("deletedDate")]
    public string? DeletedDate { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; set; }

    /// <summary>Normalized checksum without the "sha256:" prefix, upper-invariant. Null if absent.</summary>
    [JsonIgnore]
    public string? NormalizedChecksum
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Checksum)) return null;
            var c = Checksum.Trim();
            var idx = c.IndexOf(':');
            if (idx >= 0) c = c[(idx + 1)..];
            return c.Trim().ToUpperInvariant();
        }
    }

    /// <summary>Full local path to the primary file (installPath + fileName).</summary>
    [JsonIgnore]
    public string FullFilePath => System.IO.Path.Combine(InstallPath, FileName);
}
