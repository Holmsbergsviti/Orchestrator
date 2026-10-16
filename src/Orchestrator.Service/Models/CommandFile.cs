// =====================================================================================
// FILE PURPOSE (in plain terms):
//   The shape of commands.json in your control repo — one-off admin commands the operator
//   sends to specific machines (e.g. shut down, restart, capture a screenshot). Each machine
//   id maps to a single pending command/request carrying a unique id (nonce). The agent acts
//   on an id only when it hasn't seen it before, so a "shutdown" (or a screenshot capture)
//   doesn't loop every time the machine comes back up or re-syncs.
// =====================================================================================

using System.Globalization;              // for culture-independent timestamp parsing
using System.Text.Json.Serialization;   // for [JsonPropertyName]

namespace Orchestrator.Service.Models;

/// <summary>Root of commands.json: per-machine commands, Wake-on-LAN requests, and screenshot requests.</summary>
public sealed class CommandFile
{
    [JsonPropertyName("commands")]
    public Dictionary<string, MachineCommand> Commands { get; set; } = new();

    /// <summary>Wake-on-LAN requests processed by whichever machine is the waker (targets are off).</summary>
    [JsonPropertyName("wake")]
    public List<WakeRequest> Wake { get; set; } = new();

    /// <summary>Per-machine pending screenshot capture requests (nonce-gated, same pattern as Commands).</summary>
    [JsonPropertyName("screenshots")]
    public Dictionary<string, ScreenshotRequest> Screenshots { get; set; } = new();

    /// <summary>Per-machine pending live remote-control session requests (nonce-gated, same
    /// pattern as Commands/Screenshots).</summary>
    [JsonPropertyName("remoteSessions")]
    public Dictionary<string, RemoteSessionRequest> RemoteSessions { get; set; } = new();
}

/// <summary>A request to start a live remote-control session on one machine. Unlike a
/// screenshot's single capture, this schedules a longer-lived interactive process that
/// streams frames and (once connected) accepts mouse/keyboard input via the console's relay.</summary>
public sealed class RemoteSessionRequest
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;     // nonce; also the relay auth token
    [JsonPropertyName("requestedUtc")] public string? RequestedUtc { get; set; }
    /// <summary>If the agent's next sync happens after this, the request is stale and is
    /// consumed without starting a session (the operator has presumably given up waiting).</summary>
    [JsonPropertyName("expiresUtc")] public string? ExpiresUtc { get; set; }
}

/// <summary>A request to capture the screen on one machine, dropped by the console and picked
/// up by that machine's agent, which schedules the actual capture in the logged-on user's
/// interactive session (a Windows service has no desktop of its own to capture).</summary>
public sealed class ScreenshotRequest
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;     // nonce (captured once per request)
    [JsonPropertyName("requestedUtc")] public string? RequestedUtc { get; set; }
    [JsonPropertyName("expiresUtc")] public string? ExpiresUtc { get; set; }    // skipped if first seen after this
}

/// <summary>One Wake-on-LAN request: send a magic packet to this MAC.</summary>
public sealed class WakeRequest
{
    [JsonPropertyName("mac")] public string Mac { get; set; } = string.Empty;   // target NIC MAC
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;     // nonce (sent once per waker)
    [JsonPropertyName("machineId")] public string? MachineId { get; set; }      // for logging/traceability
    [JsonPropertyName("requestedUtc")] public string? RequestedUtc { get; set; }
    [JsonPropertyName("expiresUtc")] public string? ExpiresUtc { get; set; }    // skipped if first seen after this
}

/// <summary>A single pending admin command for one machine.</summary>
public sealed class MachineCommand
{
    /// <summary>What to do: "shutdown" or "restart".</summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    /// <summary>Unique id (nonce). A new value = a new command to run once.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("requestedUtc")]
    public string? RequestedUtc { get; set; }

    /// <summary>If the agent first sees this command after this time, it is consumed without running.</summary>
    [JsonPropertyName("expiresUtc")]
    public string? ExpiresUtc { get; set; }
}

/// <summary>
/// Decides whether a one-shot request is too old to act on. Each machine remembers the ids it
/// already ran only in its local config.json, so without an expiry a reinstalled machine (or a
/// newly added waker) would replay every request still sitting in commands.json — a restart
/// from months ago included.
/// </summary>
public static class OneShotExpiry
{
    /// <summary>Restart/shutdown: still wanted when a briefly-offline machine comes back.</summary>
    public static readonly TimeSpan CommandMaxAge = TimeSpan.FromHours(24);

    /// <summary>Screenshot / wake: only useful while the operator is waiting.</summary>
    public static readonly TimeSpan InteractiveMaxAge = TimeSpan.FromHours(1);

    /// <summary>
    /// True when the request has expired. Uses expiresUtc when present; for requests written by
    /// an older console (no expiresUtc) falls back to requestedUtc + <paramref name="maxAge"/>.
    /// A request carrying neither date is treated as current (the old behaviour).
    /// </summary>
    public static bool IsExpired(string? expiresUtc, string? requestedUtc, TimeSpan maxAge, DateTimeOffset nowUtc)
    {
        if (TryParseUtc(expiresUtc, out var expires)) return nowUtc > expires;
        if (TryParseUtc(requestedUtc, out var requested)) return nowUtc > requested + maxAge;
        return false;
    }

    // Culture-independent, and a timestamp with no offset is read as UTC (the console always writes "O" format).
    private static bool TryParseUtc(string? s, out DateTimeOffset value)
        => DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
}
