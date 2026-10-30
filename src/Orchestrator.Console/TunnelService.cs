// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Lets PCs on OTHER networks reach this console for a live screen session without the
//   operator's IP address ever being shared or opened up. When 'Console:Tunnel' is on, the
//   console starts Cloudflare's free "quick tunnel" program (cloudflared) as a child process.
//   cloudflared dials OUT to Cloudflare and is handed back a random public name such as
//   https://quiet-fox-1234.trycloudflare.com; anything sent to that name is carried back to a
//   small private listener on this computer (see Program.cs: it serves ONLY the agent's relay
//   path, never the operator's pages or API). That name — not an IP — is what the signed relay
//   hint tells a PC to dial, and it changes every time the tunnel starts.
//
//   The tunnel carries the screen frames, so Cloudflare can see them in transit (they sit behind
//   Cloudflare's own HTTPS certificate, which is why no thumbprint is pinned for it). The relay's
//   per-session token and the PC's signature still decide who is let in.
//
//   If cloudflared isn't installed or dies, the console keeps working: there is simply no tunnel
//   address to advertise and PCs fall back to the console's LAN name (see RelayAdvert).
// =====================================================================================

using System.ComponentModel;                    // Win32Exception: "program not found"
using System.Diagnostics;                       // Process
using System.Text.RegularExpressions;           // reading the public name out of cloudflared's output

namespace Orchestrator.Console;

public sealed partial class TunnelService(ConsoleOptions opt, ILogger<TunnelService> log) : BackgroundService
{
    // The public name cloudflared prints once it has made a quick tunnel.
    [GeneratedRegex(@"https://(?<host>[a-z0-9][a-z0-9\-]{0,62}\.trycloudflare\.com)", RegexOptions.IgnoreCase)]
    private static partial Regex PublicNameRx();

    // cloudflared prints this once its connection to Cloudflare is actually up. The name is printed
    // a few seconds BEFORE that, and handing it out earlier would send a PC to a name that isn't live yet.
    private const string UpMarker = "Registered tunnel connection";

    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(15);

    private volatile RelayAdvert? _advert;   // set only while the tunnel is up

    /// <summary>What a PC should dial through the tunnel right now, or null when it isn't up.
    /// No thumbprint: Cloudflare presents a normal public certificate, so ordinary validation applies.</summary>
    public RelayAdvert? Advert => _advert;

    /// <summary>Set (or clear) the address. Used by the process watcher below, and by tests that
    /// need a tunnel without running cloudflared.</summary>
    public void SetAdvert(RelayAdvert? advert) => _advert = advert;

    /// <summary>Pull the public host name out of one line of cloudflared output, or null.</summary>
    public static string? ParsePublicHost(string? line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        var m = PublicNameRx().Match(line);
        if (!m.Success) return null;
        var host = m.Groups["host"].Value.ToLowerInvariant();
        return host == "api.trycloudflare.com" ? null : host;   // cloudflared's own control endpoint, not a tunnel
    }

    /// <summary>True for the line that says the tunnel is connected.</summary>
    public static bool IsUpLine(string? line) => line is not null && line.Contains(UpMarker, StringComparison.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!opt.Tunnel) return;   // off by default: nothing is started, nothing is opened
        while (!ct.IsCancellationRequested)
        {
            var neverStarted = false;
            try { neverStarted = !await RunOnceAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { log.LogWarning(ex, "tunnel: stopped unexpectedly"); }
            finally { SetAdvert(null); }   // a dead tunnel must never be advertised

            if (neverStarted) return;   // cloudflared isn't there: retrying every 15 s would only spam the log
            try { await Task.Delay(RestartDelay, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Run cloudflared until it exits. Returns false if it could not be started at all.</summary>
    private async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo(opt.TunnelCommand)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,    // cloudflared logs to stderr
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // Point it at the private loopback listener, never at the main console port.
        foreach (var a in new[] { "tunnel", "--no-autoupdate", "--url", $"http://127.0.0.1:{opt.TunnelPort}" })
            psi.ArgumentList.Add(a);

        Process proc;
        try { proc = Process.Start(psi) ?? throw new InvalidOperationException("no process"); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            log.LogError("tunnel: could not start '{Cmd}' ({Why}). Install cloudflared (macOS: brew install cloudflared; " +
                         "Windows: winget install Cloudflare.cloudflared) or set Console:TunnelCommand to its full path. " +
                         "Until then PCs on other networks can't reach this console.", opt.TunnelCommand, ex.Message);
            return false;
        }

        using (proc)
        using (ct.Register(() => Kill(proc)))   // the tunnel must not outlive the console
        {
            string? host = null;
            async Task Pump(StreamReader r)
            {
                while (await r.ReadLineAsync(CancellationToken.None) is { } line)
                {
                    host ??= ParsePublicHost(line);
                    if (host is not null && IsUpLine(line) && _advert is null)
                    {
                        SetAdvert(new RelayAdvert($"wss://{host}", ""));
                        log.LogInformation("tunnel: up. PCs on any network are told to dial through Cloudflare (address changes on every start).");
                    }
                }
            }
            await Task.WhenAll(Pump(proc.StandardError), Pump(proc.StandardOutput));
            await proc.WaitForExitAsync(CancellationToken.None);
            log.LogWarning("tunnel: cloudflared exited (code {Code}); restarting in {Delay}s", proc.ExitCode, (int)RestartDelay.TotalSeconds);
        }
        return true;
    }

    private static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (Exception) { /* already gone */ }
    }
}
