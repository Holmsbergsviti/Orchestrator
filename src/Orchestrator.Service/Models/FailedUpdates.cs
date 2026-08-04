// =====================================================================================
// FILE PURPOSE (in plain terms):
//   The shape of cache\failed-updates.json — the list of agent builds this machine tried,
//   found broken, and rolled back from.
//
//   It exists to break a loop. When update-agent.ps1 rejects a build it restores the previous
//   binary, which means the machine is once again NOT running the published build — so on the
//   very next sync the updater would see the same mismatch, download the same broken build,
//   install it, watch it fail, roll back... indefinitely, on every machine at once. Recording
//   the rejected hash here is what makes a rollback stick.
//
//   Written by update-agent.ps1 (PowerShell, running as SYSTEM after the service is stopped)
//   and read by SelfUpdateService, which is why it's a plain file rather than in-memory state.
// =====================================================================================

using System.Text.Json.Serialization;   // for [JsonPropertyName] JSON mapping

namespace Orchestrator.Service.Models;

/// <summary>One build that was installed, failed its health check, and was rolled back.</summary>
public sealed class FailedUpdate
{
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("utc")]
    public string? Utc { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

/// <summary>Root of failed-updates.json.</summary>
public sealed class FailedUpdates
{
    [JsonPropertyName("failed")]
    public List<FailedUpdate> Failed { get; set; } = new();

    /// <summary>True if this exact build has already been tried and rejected here. Comparison is
    /// normalized because the hash is written by PowerShell and read by C#, and a casing or
    /// formatting difference silently reintroducing the crash loop would be a miserable bug.</summary>
    public bool IsQuarantined(string? sha256)
    {
        if (string.IsNullOrWhiteSpace(sha256)) return false;
        var want = Normalize(sha256);
        if (want.Length == 0) return false;
        foreach (var f in Failed)
            if (Normalize(f.Sha256) == want) return true;
        return false;
    }

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? ""
            : new string(value.Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
}
