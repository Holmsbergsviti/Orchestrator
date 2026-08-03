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
//
//   Failures are made VISIBLE rather than silent: the banner comes up before the connection
//   is attempted and, if the session can't start, stays up for a few seconds showing why.
//   This process runs in the logged-on user's session, so that banner is the only feedback
//   channel it has to a human — the operator at the console just sees "no frames", and the
//   log file lives on a machine they may not be sitting at.
// =====================================================================================

using System.Net.Sockets;             // SocketException (used to explain "couldn't reach the console")
using System.Net.WebSockets;          // ClientWebSocket
using System.Runtime.Versioning;      // [SupportedOSPlatform]
using System.Security.Authentication; // AuthenticationException (TLS/certificate failures)
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
    private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromSeconds(5);      // between connect attempts
    private static readonly TimeSpan ErrorDisplay = TimeSpan.FromSeconds(20);          // how long a failure stays on screen
    private const int ConnectAttempts = 3;   // the console may be restarting right as the task fires

    private const string ConnectingText = "Remote control — connecting…";
    private const string ActiveText = "Remote control active — click to end";

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

        var machine = _configService.LoadOrCreateMachineConfig();
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        sessionCts.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, _config.RemoteSessionMaxMinutes)));

        // The banner goes up FIRST, before anything can fail, so that "nothing happened at all"
        // is never one of the outcomes someone at this machine has to interpret.
        using var banner = new SessionBanner(ConnectingText, SessionBanner.ColorPending, () => SafeCancel(sessionCts));
        var uiThread = new Thread(banner.RunMessageLoop) { IsBackground = true };
        uiThread.SetApartmentState(ApartmentState.STA);   // conventional for a Win32 message-loop thread
        uiThread.Start();

        try
        {
            if (string.IsNullOrWhiteSpace(_config.RelayUrl))
            {
                _log.LogWarning("remote-session {Id}: 'Orchestrator:RelayUrl' is not configured — nothing to connect to", sessionId);
                // sessionCts (not ct): clicking the banner cancels THAT, and dismissing the
                // message should stop the wait rather than leave the process hanging around.
                await ShowFailureAsync(banner, "Remote control isn't set up on this PC: 'Orchestrator:RelayUrl' is empty in appsettings.json.", sessionCts.Token);
                return 1;
            }

            _log.LogInformation("remote-session {Id}: started (max {Max}min)", sessionId, _config.RemoteSessionMaxMinutes);
            return await RunSessionLoopAsync(machine, sessionId, banner, sessionCts.Token);
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

    /// <summary>Leave a failure reason on screen long enough to be read (or until whoever is at
    /// the machine clicks the banner away). This is the only place a person ever finds out why
    /// a requested session didn't start.</summary>
    private static async Task ShowFailureAsync(SessionBanner banner, string reason, CancellationToken ct)
    {
        banner.SetState(reason, SessionBanner.ColorPending);
        try { await Task.Delay(ErrorDisplay, ct); } catch (OperationCanceledException) { /* dismissed or shutting down */ }
    }

    private async Task<int> RunSessionLoopAsync(MachineConfig machine, string sessionId, SessionBanner banner, CancellationToken ct)
    {
        var uri = new Uri(_config.RelayUrl.TrimEnd('/') + $"/relay/agent?machineId={Uri.EscapeDataString(machine.MachineId)}");

        var (ws, error) = await ConnectWithRetriesAsync(uri, sessionId, ct);
        if (ws is null)
        {
            await ShowFailureAsync(banner, $"Remote control couldn't start: {error}", ct);
            return 1;
        }

        using (ws)
        {
            _log.LogInformation("remote-session {Id}: connected to relay {Url}", sessionId, uri);
            banner.SetState(ActiveText, SessionBanner.ColorActive);

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
                    else
                    {
                        // A null capture means no desktop to grab right now (locked workstation,
                        // RDP disconnect, secure desktop). Keep the session up — it usually comes back.
                        _log.LogDebug("remote-session {Id}: screen capture unavailable this frame", sessionId);
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
        }

        _log.LogInformation("remote-session {Id}: ended", sessionId);
        return 0;
    }

    /// <summary>Try to reach the relay a few times before giving up. Returns the live socket, or
    /// null plus a short human-readable reason suitable for showing on the banner.</summary>
    private async Task<(ClientWebSocket? Socket, string? Error)> ConnectWithRetriesAsync(Uri uri, string sessionId, CancellationToken ct)
    {
        string? lastError = null;
        for (var attempt = 1; attempt <= ConnectAttempts; attempt++)
        {
            if (ct.IsCancellationRequested) return (null, "the session was ended before it connected.");

            var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("Authorization", $"Bearer {sessionId}");
            ApplyCertificatePinning(ws);
            try
            {
                await ws.ConnectAsync(uri, ct);
                return (ws, null);
            }
            catch (Exception ex)
            {
                ws.Dispose();
                lastError = Explain(ex, uri);
                _log.LogWarning(ex, "remote-session {Id}: connect attempt {Attempt}/{Total} to {Url} failed",
                    sessionId, attempt, ConnectAttempts, uri);
                if (attempt == ConnectAttempts) break;
                try { await Task.Delay(ConnectRetryDelay, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        return (null, lastError ?? "the console couldn't be reached.");
    }

    /// <summary>When a thumbprint is configured, accept exactly that certificate and nothing else.
    /// Pinning is how a self-signed console certificate is supported without weakening anything —
    /// it's stricter than normal CA trust, since only the one pinned certificate is ever valid.</summary>
    private void ApplyCertificatePinning(ClientWebSocket ws)
    {
        var pinned = NormalizeThumbprint(_config.RelayCertThumbprint);
        if (pinned.Length == 0) return;   // no pin -> normal certificate-chain validation applies

        ws.Options.RemoteCertificateValidationCallback = (_, cert, _, _) =>
            cert is not null && string.Equals(NormalizeThumbprint(cert.GetCertHashString()), pinned, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Strip everything that isn't a hex digit, so thumbprints copied from the Windows
    /// certificate dialog (spaces, colons, and its invisible left-to-right marks) still match.</summary>
    private static string NormalizeThumbprint(string? value)
        => string.IsNullOrWhiteSpace(value) ? "" : new string(value.Where(Uri.IsHexDigit).ToArray());

    /// <summary>Turn a connect exception into one sentence that names the actual fix. The full
    /// exception still goes to the log; this is what fits on a banner.</summary>
    private string Explain(Exception ex, Uri uri)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is AuthenticationException)
                return NormalizeThumbprint(_config.RelayCertThumbprint).Length == 0
                    ? $"the console's HTTPS certificate isn't trusted by this PC. If it's self-signed, set 'Orchestrator:RelayCertThumbprint' to the thumbprint the console prints at startup."
                    : $"the console's certificate didn't match 'Orchestrator:RelayCertThumbprint'. Re-copy the thumbprint the console prints at startup.";
            if (e is SocketException)
                return $"couldn't reach the console at {uri.Host}:{uri.Port}. Is it running, bound beyond localhost, and allowed through the firewall?";
        }
        if (ex.Message.Contains("401", StringComparison.Ordinal) || ex.Message.Contains("403", StringComparison.Ordinal))
            return "the console rejected this session token. It forgets pending sessions when restarted — start the session again.";
        return ex.Message;
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
