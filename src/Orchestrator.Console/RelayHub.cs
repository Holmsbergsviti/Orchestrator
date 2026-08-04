// =====================================================================================
// FILE PURPOSE (in plain terms):
//   The actual relay: bridges one agent's WebSocket (a fleet machine streaming its screen
//   and, later, accepting input) with one browser's WebSocket (the operator watching/
//   controlling it) for the lifetime of a remote-control session. A session is registered
//   here the moment the console queues the request (so the agent's one-time token is
//   recognized when it eventually connects, which can be minutes later), and is torn down
//   the moment either side disconnects — there's no "keep streaming to a dead viewer" or
//   "keep listening for input with no agent attached" state. Frames flow agent->browser;
//   input events flow browser->agent; each direction is just forwarded byte-for-byte,
//   sight unseen, by whichever side's own connection handler owns that direction.
//
//   The relay also narrates itself to the viewer: small JSON *text* messages telling the
//   browser whether it's still waiting for the agent, live, or giving up. Without those, a
//   blank viewer is ambiguous — "the agent never connected" and "the agent is connected but
//   sending nothing" look identical, and they have completely different fixes.
// =====================================================================================

using System.Collections.Concurrent;   // ConcurrentDictionary of in-flight sessions
using System.Net.WebSockets;           // WebSocket / WebSocketMessageType
using System.Text;                     // Encoding.UTF8 for the status messages

namespace Orchestrator.Console;

public sealed class RelayHub
{
    private sealed class RelaySession
    {
        public required string MachineId { get; init; }
        public DateTimeOffset ExpiresUtc { get; set; }
        public WebSocket? Viewer { get; set; }
        public WebSocket? Agent { get; set; }
        public readonly SemaphoreSlim Gate = new(1, 1);
        // A socket tolerates one concurrent reader and one concurrent writer — but the viewer
        // socket has TWO potential writers (the agent->viewer frame pump, and this class's own
        // status messages), so every write to it goes through this gate.
        public readonly SemaphoreSlim ViewerSendGate = new(1, 1);
        public readonly TaskCompletionSource BothConnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Cancelling this is how one side tells the other's pump to stop — NOT by calling
        // CloseAsync on the peer's socket directly, which would race a concurrently pending
        // ReceiveAsync on that same socket instance (unsafe: WebSocket allows one concurrent
        // read + one concurrent write, but CloseAsync itself also reads, so it can't safely
        // run from a different task while a plain ReceiveAsync is already outstanding).
        public readonly CancellationTokenSource EndedCts = new();
    }

    private readonly ConcurrentDictionary<string, RelaySession> _sessions = new();
    private readonly ILogger<RelayHub> _log;

    public RelayHub(ILogger<RelayHub> log) => _log = log;

    /// <summary>Register a session the console just queued in commands.json, so the matching
    /// agent (bearer token) and viewer (query string) connections are recognized once they
    /// eventually arrive. <paramref name="validFor"/> bounds how long this console will wait
    /// for BOTH sides to show up before giving up.</summary>
    public void RegisterPending(string sessionId, string machineId, TimeSpan validFor)
        => _sessions[sessionId] = new RelaySession { MachineId = machineId, ExpiresUtc = DateTimeOffset.UtcNow.Add(validFor) };

    /// <summary>The id of an already-live-or-pending session for this machine, or null. Used to stop
    /// a second Remote click from stacking another session on a machine that already has one —
    /// each session runs its own screen-capture loop and its own banner on that desktop.</summary>
    public string? FindSessionForMachine(string machineId)
    {
        foreach (var (id, s) in _sessions)
        {
            if (s.ExpiresUtc < DateTimeOffset.UtcNow) { _sessions.TryRemove(id, out _); continue; }
            // A session that has already ended is removed from _sessions by its own handler, so
            // anything still here is either waiting for its two sides or actively bridging.
            if (string.Equals(s.MachineId, machineId, StringComparison.OrdinalIgnoreCase)) return id;
        }
        return null;
    }

    /// <summary>True if this session id is registered, unexpired, and (if given) matches the machine.</summary>
    public bool IsValid(string sessionId, string? machineId = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var s)) return false;
        if (s.ExpiresUtc < DateTimeOffset.UtcNow) { _sessions.TryRemove(sessionId, out _); return false; }
        return machineId is null || string.Equals(s.MachineId, machineId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Attach the operator's browser socket for this session. Blocks until the session ends.</summary>
    public Task RunViewerAsync(string sessionId, WebSocket socket, CancellationToken ct) => AttachAsync(sessionId, socket, isAgent: false, ct);

    /// <summary>Attach the agent's socket for this session. Blocks until the session ends.</summary>
    public Task RunAgentAsync(string sessionId, WebSocket socket, CancellationToken ct) => AttachAsync(sessionId, socket, isAgent: true, ct);

    private async Task AttachAsync(string sessionId, WebSocket socket, bool isAgent, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(sessionId, out var s))
        {
            await CloseAsync(socket, "unknown or expired session");
            return;
        }

        await s.Gate.WaitAsync(ct);
        try
        {
            if (isAgent)
            {
                if (s.Agent is not null) { await CloseAsync(socket, "session already claimed"); return; }
                s.Agent = socket;
            }
            else
            {
                if (s.Viewer is not null) { await CloseAsync(socket, "session already claimed"); return; }
                s.Viewer = socket;
            }
            if (s.Agent is not null && s.Viewer is not null) s.BothConnected.TrySetResult();
        }
        finally { s.Gate.Release(); }

        _log.LogInformation("relay: {Side} connected for session {Id} (machine {MachineId})", isAgent ? "agent" : "viewer", sessionId, s.MachineId);

        using var linkedCt = CancellationTokenSource.CreateLinkedTokenSource(ct, s.EndedCts.Token);

        // Wait (bounded) for the other side to show up — the agent may take up to a full sync cycle.
        var remaining = s.ExpiresUtc - DateTimeOffset.UtcNow;
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCt.Token);
        waitCts.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(1));
        if (!isAgent)
            await SendStatusAsync(s, $"{{\"t\":\"status\",\"state\":\"waiting\",\"untilUtc\":\"{s.ExpiresUtc.UtcDateTime:O}\"}}", linkedCt.Token);
        try
        {
            await s.BothConnected.Task.WaitAsync(waitCts.Token);
        }
        catch (OperationCanceledException)
        {
            SafeCancel(s.EndedCts);
            // Say WHICH side never arrived: "the agent never connected" and "you closed the
            // viewer" are the same timeout here but completely different problems to chase.
            await CloseAsync(socket, isAgent ? "no viewer connected in time" : "the agent never connected");
            _sessions.TryRemove(sessionId, out _);
            return;
        }

        var other = isAgent ? s.Viewer : s.Agent;
        if (other is null) { await CloseAsync(socket, "internal error: peer missing"); return; }

        if (!isAgent)
            await SendStatusAsync(s, "{\"t\":\"status\",\"state\":\"connected\"}", linkedCt.Token);

        try
        {
            // Pump FROM this handler's OWN socket TO the peer. The peer's own connection
            // handler is doing the same in the other direction concurrently — one concurrent
            // reader and one concurrent writer per socket is the safe, documented pattern.
            await PumpAsync(socket, other, isAgent ? s.ViewerSendGate : null, linkedCt.Token);
        }
        finally
        {
            // Say goodbye to the viewer BEFORE tearing anything down. Cancelling a pending
            // ReceiveAsync aborts that socket, so once the teardown below starts, the viewer's
            // close frame can no longer carry a reason and the browser only sees a bare 1006.
            if (isAgent)
                await SendStatusAsync(s, "{\"t\":\"status\",\"state\":\"ended\",\"reason\":\"the machine ended the session\"}", CancellationToken.None);

            // Tell the peer's pump (blocked in ReceiveAsync on ITS OWN socket) to stop, via
            // cancellation — never by touching the peer's socket from here.
            SafeCancel(s.EndedCts);
            var removedHere = _sessions.TryRemove(sessionId, out _);
            await CloseAsync(socket, "session ended");
            if (removedHere) _log.LogInformation("relay: session {Id} ended", sessionId);
        }
    }

    private static void SafeCancel(CancellationTokenSource cts)
    {
        try { cts.Cancel(); } catch (ObjectDisposedException) { /* already tearing down */ }
    }

    /// <summary>Send one status line to the viewer. Best-effort: a viewer that has already gone
    /// away is not a reason to disturb the session's own teardown path.</summary>
    private static async Task SendStatusAsync(RelaySession s, string json, CancellationToken ct)
    {
        var viewer = s.Viewer;
        if (viewer is null || viewer.State != WebSocketState.Open) return;
        try
        {
            await s.ViewerSendGate.WaitAsync(ct);
            try { await viewer.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, ct); }
            finally { s.ViewerSendGate.Release(); }
        }
        catch { /* best effort — the viewer may be mid-disconnect */ }
    }

    /// <summary><paramref name="toGate"/> must be supplied whenever the destination socket can
    /// also be written to from elsewhere (the viewer). Null means this pump is the only writer.</summary>
    private static async Task PumpAsync(WebSocket from, WebSocket to, SemaphoreSlim? toGate, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var holdingGate = false;   // true while we're partway through forwarding one fragmented message
        try
        {
            while (from.State == WebSocketState.Open && to.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                try { result = await from.ReceiveAsync(new ArraySegment<byte>(buffer), ct); }
                catch { break; }
                if (result.MessageType == WebSocketMessageType.Close) break;
                try
                {
                    // A screen frame is far bigger than this buffer, so it arrives (and is
                    // re-sent) as several fragments. Hold the gate from the first fragment to
                    // the last: slipping another message's frames in between would corrupt
                    // both messages, since WebSocket data frames can't be interleaved.
                    if (toGate is not null && !holdingGate) { await toGate.WaitAsync(ct); holdingGate = true; }
                    await to.SendAsync(new ArraySegment<byte>(buffer, 0, result.Count), result.MessageType, result.EndOfMessage, ct);
                    if (holdingGate && result.EndOfMessage) { toGate!.Release(); holdingGate = false; }
                }
                catch { break; }
            }
        }
        finally
        {
            if (holdingGate) toGate!.Release();
        }
    }

    private static async Task CloseAsync(WebSocket socket, string reason)
    {
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None);
        }
        catch { /* best effort — the connection may already be gone */ }
    }
}
