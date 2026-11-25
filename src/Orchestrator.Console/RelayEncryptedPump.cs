// =====================================================================================
// FILE PURPOSE (in plain terms):
//   The two "pumps" the relay uses instead of plain byte-forwarding when a session is
//   end-to-end encrypted (see RelayCipher). The agent's socket (which runs through the tunnel)
//   carries only sealed messages; the viewer's socket (your own browser, on this computer)
//   carries ordinary frames and input. These pumps are the seam between the two:
//     - FromAgentAsync  : opens each sealed message from the PC and hands the plain frame to the viewer.
//     - FromViewerAsync : seals each input message from the viewer and sends it to the PC.
//   A message is reassembled in full first (a screen frame arrives in many fragments) and
//   anything that doesn't open — wrong key, altered, replayed, or simply not encrypted because
//   the agent is an older build — ends the session with a reason the operator can read.
// =====================================================================================

using System.Net.WebSockets;           // WebSocket
using System.Security.Cryptography;    // CryptographicException

namespace Orchestrator.Console;

public static class RelayEncryptedPump
{
    /// <summary>Largest sealed message accepted from a PC (a full-screen JPEG is well under this).</summary>
    public const int MaxFromAgent = 32 * 1024 * 1024;
    /// <summary>Largest message accepted from the viewer (input events are tiny).</summary>
    public const int MaxFromViewer = 64 * 1024;

    /// <summary>Read one whole WebSocket message. Returns null when the peer closed, the socket
    /// failed, or the message is bigger than <paramref name="max"/> (which is treated as a failure).</summary>
    private static async Task<(WebSocketMessageType Type, byte[] Data)?> ReceiveWholeAsync(WebSocket from, int max, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result;
            try { result = await from.ReceiveAsync(new ArraySegment<byte>(buffer), ct); }
            catch { return null; }
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (message.Length + result.Count > max) return null;
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return (result.MessageType, message.ToArray());
        }
    }

    /// <summary>PC -> viewer. <paramref name="onFailure"/> gets a plain-language reason when it gives up.</summary>
    public static async Task FromAgentAsync(WebSocket agent, WebSocket viewer, SemaphoreSlim viewerGate, RelayCipher cipher,
        Action<string> onFailure, CancellationToken ct)
    {
        while (agent.State == WebSocketState.Open && viewer.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var received = await ReceiveWholeAsync(agent, MaxFromAgent, ct);
            if (received is null) return;   // closed, failed or oversized: the session ends

            byte[] payload; bool isText;
            try
            {
                if (received.Value.Type != WebSocketMessageType.Binary) throw new CryptographicException("not a sealed message");
                (isText, payload) = cipher.Open(received.Value.Data);
            }
            catch (CryptographicException)
            {
                // The usual cause by far: an agent build from before encryption existed, which sends frames
                // in the clear. Anything else (a tampered or replayed message) ends up here too, deliberately.
                onFailure("the PC's messages could not be decrypted. If its agent is out of date, it needs to update first; " +
                          "otherwise something altered the traffic.");
                return;
            }

            try
            {
                await viewerGate.WaitAsync(ct);
                try { await viewer.SendAsync(payload.AsMemory(), isText ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, ct); }
                finally { viewerGate.Release(); }
            }
            catch { return; }
        }
    }

    /// <summary>Viewer -> PC. The agent socket has exactly one writer (this pump), so no gate is needed.</summary>
    public static async Task FromViewerAsync(WebSocket viewer, WebSocket agent, RelayCipher cipher, CancellationToken ct)
    {
        while (viewer.State == WebSocketState.Open && agent.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var received = await ReceiveWholeAsync(viewer, MaxFromViewer, ct);
            if (received is null) return;
            try
            {
                var sealedMessage = cipher.Seal(received.Value.Type == WebSocketMessageType.Text, received.Value.Data);
                await agent.SendAsync(sealedMessage.AsMemory(), WebSocketMessageType.Binary, true, ct);
            }
            catch { return; }
        }
    }
}
