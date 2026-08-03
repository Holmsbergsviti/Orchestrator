// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Runs ONE live remote-control session end-to-end: shows the mandatory on-screen banner
//   (SessionBanner) on a dedicated UI thread, opens an outbound WebSocket to the console's
//   relay, and loops capturing the screen (reusing ScreenCaptureService) and sending each
//   frame while the session is active. This is the interactive, long-running counterpart to
//   ScreenshotService's one-shot capture-and-upload. Phase 2 is view-only: incoming WebSocket
//   messages (mouse/keyboard input from the console) are received but not yet acted on —
//   that's wired up in RemoteInputInjector once Phase 3 lands. Whatever ends first — the
//   banner being closed, the relay disconnecting, or the configured hard timeout — ends the
//   whole session; there's no path that keeps streaming or listening for input after that.
// =====================================================================================

using System.Net.WebSockets;          // ClientWebSocket
using System.Runtime.Versioning;      // [SupportedOSPlatform]
using Microsoft.Extensions.Logging;   // logging
using Orchestrator.Service.Models;    // OrchestratorConfig / MachineConfig

namespace Orchestrator.Service.Services;

public interface IRemoteSessionService
{
    /// <summary>Run one remote-control session end-to-end until it ends. Returns a process exit code.</summary>
    Task<int> RunAsync(string sessionId, CancellationToken ct = default);
}

[SupportedOSPlatform("windows")]
public sealed class RemoteSessionService : IRemoteSessionService
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(200);   // ~5 fps

    private readonly IScreenCaptureService _capture;
    private readonly IConfigService _configService;
    private readonly ILogger<RemoteSessionService> _log;
    private readonly OrchestratorConfig _config;

    public RemoteSessionService(IScreenCaptureService capture, IConfigService configService, ILogger<RemoteSessionService> log)
    {
        _capture = capture;
        _configService = configService;
        _config = configService.Config;
        _log = log;
    }

    public async Task<int> RunAsync(string sessionId, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            _log.LogWarning("remote-session: not supported on this platform");
            return 1;
        }
        if (string.IsNullOrWhiteSpace(_config.RelayUrl))
        {
            _log.LogWarning("remote-session {Id}: 'Orchestrator:RelayUrl' is not configured", sessionId);
            return 1;
        }

        var machine = _configService.LoadOrCreateMachineConfig();
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        sessionCts.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, _config.RemoteSessionMaxMinutes)));

        using var banner = new SessionBanner(() => SafeCancel(sessionCts));
        var uiThread = new Thread(banner.RunMessageLoop) { IsBackground = true };
        uiThread.SetApartmentState(ApartmentState.STA);   // conventional for a Win32 message-loop thread
        uiThread.Start();

        try
        {
            _log.LogInformation("remote-session {Id}: started (max {Max}min)", sessionId, _config.RemoteSessionMaxMinutes);
            return await RunSessionLoopAsync(machine, sessionId, sessionCts.Token);
        }
        finally
        {
            // RequestClose() only reaches an already-created window; if the session ends
            // before the banner has finished being created on its own thread (e.g. an
            // immediate relay connect failure), the first request would land too early and
            // never get retried. Poll briefly instead of a single fire-and-forget call.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && !banner.WaitForClose(TimeSpan.FromMilliseconds(100)))
                banner.RequestClose();
            uiThread.Join(TimeSpan.FromSeconds(2));
        }
    }

    private static void SafeCancel(CancellationTokenSource cts)
    {
        try { cts.Cancel(); } catch (ObjectDisposedException) { /* session already finishing */ }
    }

    private async Task<int> RunSessionLoopAsync(MachineConfig machine, string sessionId, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", $"Bearer {sessionId}");
        var uri = new Uri(_config.RelayUrl.TrimEnd('/') + $"/relay/agent?machineId={Uri.EscapeDataString(machine.MachineId)}");

        try
        {
            await ws.ConnectAsync(uri, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "remote-session {Id}: could not connect to relay {Url}", sessionId, uri);
            return 1;
        }
        _log.LogInformation("remote-session {Id}: connected to relay", sessionId);

        var receiveTask = ReceiveLoopAsync(ws, sessionId, ct);
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var jpeg = _capture.CaptureJpeg();
                if (jpeg is not null)
                {
                    try
                    {
                        // .AsMemory() (not a bare byte[]) to pick the Memory<byte> overload
                        // unambiguously — WebSocket exposes both an ArraySegment<byte> and a
                        // Memory<byte> overload, and byte[] converts implicitly to either.
                        await ws.SendAsync(jpeg.AsMemory(), WebSocketMessageType.Binary, endOfMessage: true, ct);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "remote-session {Id}: frame send failed", sessionId);
                        break;
                    }
                }
                try { await Task.Delay(FrameInterval, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "session ended", CancellationToken.None);
            }
            catch { /* best effort — the connection may already be gone */ }
            await receiveTask;
        }

        _log.LogInformation("remote-session {Id}: ended", sessionId);
        return 0;
    }

    /// <summary>Drains input-event messages from the relay. Phase 2 is view-only, so these are
    /// currently ignored beyond keeping the socket's receive buffer clear; Phase 3 feeds them
    /// to a RemoteInputInjector instead.</summary>
    private async Task ReceiveLoopAsync(ClientWebSocket ws, string sessionId, CancellationToken ct)
    {
        var buffer = new byte[4096];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buffer.AsMemory(), ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (OperationCanceledException) { /* session ending */ }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "remote-session {Id}: receive loop ended", sessionId);
        }
    }
}
