// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Lets a machine that can't reach GitHub get its heartbeat out anyway, through another
//   machine on the same local network that still can. Two roles, one class, both best-
//   effort and silent on failure (this is a fallback path, not something allowed to make
//   a sync cycle noisier or slower than normal):
//     - Listener: runs for this machine's whole lifetime, listening for OTHER machines'
//       "I need relaying" broadcasts, fetching their heartbeat over a short TCP
//       connection, and committing it to GitHub exactly as that machine would have.
//     - Broadcaster: called by FleetReporter only after several heartbeat attempts in a
//       row have failed — broadcasts this machine's own request and serves the payload
//       to whichever peer answers first.
//   LAN-trust only: see LanRelayProtocol.cs for exactly what that does and doesn't allow.
// =====================================================================================

using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Orchestrator.Service.Models;

namespace Orchestrator.Service.Services;

public interface ILanRelayService
{
    /// <summary>Runs until <paramref name="ct"/> is cancelled: listens on the local network for
    /// other machines' relay requests and forwards their heartbeat to GitHub for them.
    /// Never throws.</summary>
    Task RunListenerLoopAsync(CancellationToken ct);

    /// <summary>This machine can't reach GitHub itself: broadcast for help and wait up to
    /// <paramref name="timeout"/> for a peer to pick up <paramref name="content"/> (to be written
    /// to <paramref name="path"/> with <paramref name="commitMessage"/>, exactly as this machine
    /// would have written it itself). Returns whether a peer picked it up — NOT proof it reached
    /// GitHub, this is fire-and-forget, the same best-effort spirit as the rest of fleet
    /// reporting. Never throws.</summary>
    Task<bool> TryRelayAsync(string machineId, string path, string commitMessage, byte[] content,
        TimeSpan timeout, CancellationToken ct);
}

public sealed class LanRelayService : ILanRelayService
{
    private readonly IGitHubClient _github;
    private readonly IConfigService _configService;
    private readonly ILogger<LanRelayService> _log;
    private readonly OrchestratorConfig _config;

    public LanRelayService(IGitHubClient github, IConfigService configService, ILogger<LanRelayService> log)
    {
        _github = github;
        _configService = configService;
        _config = configService.Config;
        _log = log;
    }

    public async Task RunListenerLoopAsync(CancellationToken ct)
    {
        if (!_config.LanRelayEnabled) return;

        UdpClient udp;
        try
        {
            udp = new UdpClient(_config.LanRelayPort) { EnableBroadcast = true };
        }
        catch (Exception ex)
        {
            // Common cause: another process already has this port (another agent build running
            // alongside, or the port is just taken). Not fatal — this machine simply won't be
            // able to relay for others; it can still ask for help itself via a fresh UdpClient.
            _log.LogWarning(ex, "LAN relay: could not listen on UDP port {Port}; this machine won't be able to relay for others", _config.LanRelayPort);
            return;
        }

        using (udp)
        {
            _log.LogInformation("LAN relay: listening on UDP {Port} to help any PC on this network that loses internet", _config.LanRelayPort);
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try
                {
                    received = await udp.ReceiveAsync(ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "LAN relay: receive failed (continuing)");
                    continue;
                }

                var request = LanRelayRequest.TryDecode(Encoding.UTF8.GetString(received.Buffer));
                if (request is null) continue;   // not one of ours, or malformed — ignore silently, this port sees ordinary broadcast noise too

                var machine = _configService.LoadOrCreateMachineConfig();
                if (string.Equals(request.MachineId, machine.MachineId, StringComparison.OrdinalIgnoreCase))
                    continue;   // our own broadcast looping back — nothing to do

                // Fire-and-forget: one slow or misbehaving peer must never stop us listening for the next.
                _ = HandleRequestAsync(request, received.RemoteEndPoint.Address, ct);
            }
        }
    }

    private async Task HandleRequestAsync(LanRelayRequest request, IPAddress peerAddress, CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            using var tcp = new TcpClient();
            await tcp.ConnectAsync(peerAddress, request.ReplyPort, timeoutCts.Token);

            using var stream = tcp.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var line = await reader.ReadLineAsync(timeoutCts.Token);
            if (line is null) return;

            var payload = LanRelayPayload.TryDecode(line);
            if (payload is null || !string.Equals(payload.MachineId, request.MachineId, StringComparison.OrdinalIgnoreCase))
            {
                _log.LogWarning("LAN relay: ignoring a malformed or mismatched payload from {Peer}", peerAddress);
                return;
            }

            var sha = await _github.GetFileShaAsync(payload.Path, _config.FleetStateBranch, ct);
            await _github.PutFileAsync(payload.Path, payload.Content, _config.FleetStateBranch, payload.CommitMessage, sha, ct);
            _log.LogInformation("LAN relay: forwarded {MachineId}'s heartbeat to GitHub on its behalf (it couldn't reach GitHub itself)", payload.MachineId);
        }
        catch (OperationCanceledException) { /* peer never connected / never sent its payload in time */ }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "LAN relay: could not forward heartbeat for {MachineId} (continuing)", request.MachineId);
        }
    }

    public async Task<bool> TryRelayAsync(string machineId, string path, string commitMessage, byte[] content,
        TimeSpan timeout, CancellationToken ct)
    {
        if (!_config.LanRelayEnabled) return false;

        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Any, 0);   // port 0 -> OS picks a free one
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            using var udp = new UdpClient { EnableBroadcast = true };
            var datagram = Encoding.UTF8.GetBytes(LanRelayRequest.Encode(new LanRelayRequest(machineId, port)));
            await udp.SendAsync(datagram, new IPEndPoint(IPAddress.Broadcast, _config.LanRelayPort), ct);
            _log.LogInformation("LAN relay: broadcast for help reaching GitHub (this machine has been unable to for a while)");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            using var client = await AcceptOneAsync(listener, timeoutCts.Token);
            if (client is null)
            {
                _log.LogInformation("LAN relay: no peer answered within {Seconds:N0}s", timeout.TotalSeconds);
                return false;
            }

            using var stream = client.GetStream();
            var line = LanRelayPayload.Encode(new LanRelayPayload(machineId, path, commitMessage, content)) + "\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(line), ct);
            _log.LogInformation("LAN relay: a peer on this network picked up the heartbeat to forward for us");
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "LAN relay: broadcast attempt failed");
            return false;
        }
        finally
        {
            listener?.Stop();
        }
    }

    private static async Task<TcpClient?> AcceptOneAsync(TcpListener listener, CancellationToken ct)
    {
        try
        {
            return await listener.AcceptTcpClientAsync(ct);
        }
        catch { return null; }   // covers both cancellation (timeout) and any socket failure
    }
}

/// <summary>Runs <see cref="ILanRelayService.RunListenerLoopAsync"/> for the service's whole
/// lifetime, as its own hosted service — separate from <see cref="Orchestrator.Service.Worker"/>'s
/// sync loop, since listening for other machines' broadcasts has nothing to do with this
/// machine's own sync cadence and shouldn't share a loop with it.</summary>
public sealed class LanRelayListenerHost : Microsoft.Extensions.Hosting.BackgroundService
{
    private readonly ILanRelayService _relay;

    public LanRelayListenerHost(ILanRelayService relay) => _relay = relay;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => _relay.RunListenerLoopAsync(stoppingToken);
}
