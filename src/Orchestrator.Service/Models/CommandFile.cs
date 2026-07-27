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

/// <summary>Root of commands.json: machine id -> its pending command.</summary>
public sealed class CommandFile
{
    [JsonPropertyName("commands")]
    public Dictionary<string, MachineCommand> Commands { get; set; } = new();
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
