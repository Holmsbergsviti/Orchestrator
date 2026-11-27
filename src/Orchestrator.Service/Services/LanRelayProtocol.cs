// =====================================================================================
// FILE PURPOSE (in plain terms):
//   The two tiny JSON messages the LAN relay feature exchanges: the UDP broadcast a
//   stuck machine sends asking for help, and the payload it then hands over a short TCP
//   connection to whichever peer answers. Kept separate from LanRelayService (the actual
//   sockets) so the message format can be unit-tested without opening a real socket.
//
//   Deliberately LAN-trust only, same model as the existing Wake-on-LAN broadcast: anyone
//   on the same network segment can send a relay-request. The one safety property that
//   DOES hold regardless of who sends it: TryDecode on the payload refuses anything whose
//   path isn't exactly state/<thatMachineId>.json, so even a forged request can only ever
//   result in a bogus heartbeat for SOME machine id being relayed — never a write anywhere
//   else in the repo (manifest, commands, another machine's screenshots, ...).
// =====================================================================================

using System.Text.Json;

namespace Orchestrator.Service.Services;

/// <summary>Broadcast by a machine that can't reach GitHub itself: "I'm &lt;MachineId&gt;, connect
/// back to me on &lt;ReplyPort&gt; and I'll hand you my heartbeat to forward."</summary>
public sealed record LanRelayRequest(string MachineId, int ReplyPort)
{
    public static string Encode(LanRelayRequest r) =>
        JsonSerializer.Serialize(new { t = "relay-request", machineId = r.MachineId, port = r.ReplyPort });

    /// <summary>Null for anything malformed, not ours, or with an out-of-range port. Pure, so it's
    /// unit-tested without a real socket.</summary>
    public static LanRelayRequest? TryDecode(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("t", out var t) || t.GetString() != "relay-request") return null;
            if (!root.TryGetProperty("machineId", out var m) || string.IsNullOrWhiteSpace(m.GetString())) return null;
            if (!root.TryGetProperty("port", out var p) || p.ValueKind != JsonValueKind.Number) return null;
            var port = p.GetInt32();
            if (port is < 1 or > 65535) return null;
            return new LanRelayRequest(m.GetString()!, port);
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>What the stuck machine hands over the TCP connection: the exact bytes and repo path
/// its own heartbeat would have been written to, so the peer can forward them byte-for-byte
/// without needing to understand or re-derive anything.</summary>
public sealed record LanRelayPayload(string MachineId, string Path, string CommitMessage, byte[] Content)
{
    public static string Encode(LanRelayPayload p) => JsonSerializer.Serialize(new
    {
        machineId = p.MachineId,
        path = p.Path,
        message = p.CommitMessage,
        content = Convert.ToBase64String(p.Content)
    });

    /// <summary>Null for anything malformed, or whose path isn't exactly state/&lt;machineId&gt;.json —
    /// see the file-level note on why that check matters even without per-machine authentication.</summary>
    public static LanRelayPayload? TryDecode(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var machineId = root.TryGetProperty("machineId", out var m) ? m.GetString() : null;
            var path = root.TryGetProperty("path", out var p) ? p.GetString() : null;
            var message = root.TryGetProperty("message", out var msg) ? msg.GetString() : null;
            var content = root.TryGetProperty("content", out var c) ? c.GetString() : null;
            if (string.IsNullOrWhiteSpace(machineId) || string.IsNullOrWhiteSpace(path) || content is null) return null;
            if (path != $"state/{machineId}.json") return null;   // the one hard boundary, see file header
            return new LanRelayPayload(machineId, path, message ?? "", Convert.FromBase64String(content));
        }
        catch (Exception ex) when (ex is JsonException or FormatException) { return null; }
    }
}
