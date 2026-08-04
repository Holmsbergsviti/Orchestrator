// =====================================================================================
// FILE PURPOSE (in plain terms):
//   The shape of agent.json in your CONTROL repo — the record of which agent build the fleet
//   is supposed to be running. CI writes it after building and publishing a new exe; every
//   agent reads it each sync and updates itself if it isn't running that build.
//
//   Why the checksum lives here rather than next to the binary: the binary is published to a
//   PUBLIC repo, and an agent installs it as SYSTEM. If the expected hash travelled with the
//   file, anyone who could replace the file could replace the hash too and the check would
//   prove nothing. Keeping it in the private control repo means a downloaded binary is only
//   installed if it matches a hash recorded somewhere the public repo can't reach — so
//   compromising the public repo alone doesn't get you code execution on the fleet.
// =====================================================================================

using System.Text.Json.Serialization;   // for [JsonPropertyName] JSON mapping

namespace Orchestrator.Service.Models;

/// <summary>Root of agent.json: the build every machine should be running.</summary>
public sealed class AgentRelease
{
    /// <summary>SHA-256 of the published exe, lower-case hex. An agent installs the download
    /// only if it hashes to exactly this.</summary>
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    /// <summary>Where to fetch the exe. Normally the dist branch of the code repo.</summary>
    [JsonPropertyName("exeUrl")]
    public string ExeUrl { get; set; } = "";

    /// <summary>Source commit this build came from — for humans reading the file, not used for
    /// the update decision (the hash is what actually decides).</summary>
    [JsonPropertyName("commit")]
    public string? Commit { get; set; }

    [JsonPropertyName("publishedUtc")]
    public string? PublishedUtc { get; set; }

    /// <summary>Set false to stop the fleet updating without having to unpublish anything —
    /// the brake for when a bad build is already out there.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Whether this release is usable at all: an entry with no hash or no URL can't be
    /// verified, and an unverifiable binary is one we refuse to run.</summary>
    public bool IsUsable()
        => Enabled
           && !string.IsNullOrWhiteSpace(ExeUrl)
           && NormalizedSha256().Length == 64;

    /// <summary>The hash in the one form comparisons use: lower-case, no spaces or separators.</summary>
    public string NormalizedSha256()
    {
        if (string.IsNullOrWhiteSpace(Sha256)) return "";
        var chars = Sha256.Where(Uri.IsHexDigit).ToArray();
        return new string(chars).ToLowerInvariant();
    }

    /// <summary>True if a machine already running <paramref name="currentSha256"/> should install
    /// this release. Comparing the hash of the RUNNING exe (rather than a version number it
    /// reports about itself) means a machine that was rolled back, patched by hand, or failed
    /// halfway through an update still converges on the published build.</summary>
    public bool ShouldUpdate(string? currentSha256)
    {
        if (!IsUsable()) return false;
        var current = string.IsNullOrWhiteSpace(currentSha256)
            ? ""
            : new string(currentSha256.Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
        if (current.Length != 64) return false;   // couldn't hash ourselves; don't guess
        return !string.Equals(current, NormalizedSha256(), StringComparison.Ordinal);
    }
}
