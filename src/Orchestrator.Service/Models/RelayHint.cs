// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Tells an agent WHERE to dial for a live remote-control session, at the moment the
//   operator asks for one — instead of that address being baked into every PC at install
//   time. The console publishes a small signed file (relay-hint.json) on a throwaway branch
//   (relay-hint) of the control repo, names the session it is for, and deletes the branch
//   again once the session is over. So the console can run from any computer, its address is
//   never stored on the PCs or in the repo's normal history, and nothing is left behind.
//
//   The file is signed with the operator's key like commands.json, and an agent only honours
//   it when its build enforces signatures: otherwise anyone holding a copied token could
//   point a PC's screen at their own server. Builds that don't enforce signing keep using
//   the Orchestrator:RelayUrl setting.
//
//   Whatever the file says ends up on a command line, so every value is checked against a
//   strict pattern first and dropped if it doesn't match.
// =====================================================================================

using System.Text.RegularExpressions;   // the strict value patterns
using System.Text.Json.Serialization;   // [JsonPropertyName]

namespace Orchestrator.Service.Models;

/// <summary>Root of relay-hint.json: one entry per machine that has a session waiting.</summary>
public sealed class RelayHintFile
{
    [JsonPropertyName("sessions")]
    public Dictionary<string, RelayHintEntry> Sessions { get; set; } = new();
}

/// <summary>Where one machine should connect for one session.</summary>
public sealed class RelayHintEntry
{
    /// <summary>The session this is for — must equal the id in the (signed) commands.json request.</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    /// <summary>ws:// or wss:// plus a host and optional port. No path, no credentials.</summary>
    [JsonPropertyName("relayUrl")] public string RelayUrl { get; set; } = string.Empty;
    /// <summary>SHA-1 thumbprint to pin, or empty to use normal certificate validation.</summary>
    [JsonPropertyName("relayCertThumbprint")] public string? RelayCertThumbprint { get; set; }
}

public static class RelayHint
{
    /// <summary>The throwaway branch the hint lives on, and the file inside it.</summary>
    public const string Branch = "relay-hint";
    public const string Path = "relay-hint.json";

    // scheme://host[:port] and nothing else. The host class is deliberately narrow (letters,
    // digits, dots, hyphens, and the brackets/colons an IPv6 literal needs).
    private static readonly Regex UrlPattern = new(@"^wss?://[A-Za-z0-9.\-\[\]:]{1,253}$", RegexOptions.CultureInvariant);
    private static readonly Regex ThumbprintPattern = new(@"^[0-9A-Fa-f]{40}$", RegexOptions.CultureInvariant);

    public static bool IsWellFormedUrl(string? url) => url is not null && UrlPattern.IsMatch(url) && Uri.TryCreate(url, UriKind.Absolute, out _);

    /// <summary>Empty is fine (no pinning); otherwise exactly 40 hex digits, no separators.</summary>
    public static bool IsWellFormedThumbprint(string? thumbprint) => string.IsNullOrEmpty(thumbprint) || ThumbprintPattern.IsMatch(thumbprint);

    /// <summary>The usable (url, thumbprint) for this machine and this exact session, or null when
    /// the file has nothing for it or what it has is malformed.</summary>
    public static (string Url, string Thumbprint)? Pick(RelayHintFile? file, string machineId, string sessionId)
    {
        if (file is null) return null;
        if (!file.Sessions.TryGetValue(machineId, out var entry))
            entry = file.Sessions.FirstOrDefault(kv => string.Equals(kv.Key, machineId, StringComparison.OrdinalIgnoreCase)).Value;
        if (entry is null || entry.Id != sessionId) return null;   // a hint for some other session is not ours
        if (!IsWellFormedUrl(entry.RelayUrl) || !IsWellFormedThumbprint(entry.RelayCertThumbprint)) return null;
        return (entry.RelayUrl, entry.RelayCertThumbprint ?? "");
    }
}
