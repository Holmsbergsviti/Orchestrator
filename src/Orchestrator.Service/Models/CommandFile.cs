// =====================================================================================
// FILE PURPOSE (in plain terms):
//   The shape of commands.json in your control repo — one-off admin commands the operator
//   sends to specific machines (e.g. shut down, restart, capture a screenshot). Each machine
//   id maps to a single pending command/request carrying a unique id (nonce). The agent acts
//   on an id only when it hasn't seen it before, so a "shutdown" (or a screenshot capture)
//   doesn't loop every time the machine comes back up or re-syncs.
// =====================================================================================

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
}

/// <summary>One Wake-on-LAN request: send a magic packet to this MAC.</summary>
public sealed class WakeRequest
{
    [JsonPropertyName("mac")] public string Mac { get; set; } = string.Empty;   // target NIC MAC
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;     // nonce (sent once per waker)
    [JsonPropertyName("machineId")] public string? MachineId { get; set; }      // for logging/traceability
    [JsonPropertyName("requestedUtc")] public string? RequestedUtc { get; set; }
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
}
