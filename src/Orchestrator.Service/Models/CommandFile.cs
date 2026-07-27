// =====================================================================================
// FILE PURPOSE (in plain terms):
//   The shape of commands.json in your control repo — one-off admin commands the operator
//   sends to specific machines (e.g. shut down, restart). Each machine id maps to a single
//   pending command carrying a unique id (nonce). The agent runs a command only when it
//   sees an id it hasn't run before, so a "shutdown" doesn't loop every time the machine
//   comes back up.
// =====================================================================================

using System.Text.Json.Serialization;   // for [JsonPropertyName]

namespace Orchestrator.Service.Models;

/// <summary>Root of commands.json: per-machine commands plus Wake-on-LAN requests.</summary>
public sealed class CommandFile
{
    [JsonPropertyName("commands")]
    public Dictionary<string, MachineCommand> Commands { get; set; } = new();

    /// <summary>Wake-on-LAN requests processed by whichever machine is the waker (targets are off).</summary>
    [JsonPropertyName("wake")]
    public List<WakeRequest> Wake { get; set; } = new();
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
