// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Works out what the console tells a PC when the operator asks for a live screen session:
//   "dial wss://<this computer's name>:5080, and expect a certificate with this thumbprint".
//   It prefers this computer's NAME over an IP address (a name keeps working when the address
//   changes, and isn't an address at all), and can be overridden with Console:RelayAdvertiseUrl
//   for setups where the PCs reach the console some other way (a VPN name, say).
//
//   A console bound only to localhost has nothing useful to advertise, so it advertises nothing
//   and the PCs fall back to their installed Orchestrator:RelayUrl, as before.
// =====================================================================================

using System.Net;   // Dns

namespace Orchestrator.Console;

public sealed record RelayAdvert(string Url, string Thumbprint)
{
    /// <summary>What to advertise for this console, or null when there is nothing a PC elsewhere could use.</summary>
    /// <param name="hostName">This computer's name (a test seam; defaults to the real one).</param>
    public static RelayAdvert? From(ConsoleOptions opt, string listenUrls, string? certThumbprint, string? hostName = null, bool? isMac = null)
    {
        var thumb = certThumbprint ?? "";

        // An explicit address wins, if it is a usable ws(s):// one.
        if (!string.IsNullOrWhiteSpace(opt.RelayAdvertiseUrl))
        {
            var url = opt.RelayAdvertiseUrl.Trim().TrimEnd('/');
            return Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme is "ws" or "wss") && u.AbsolutePath == "/"
                ? new RelayAdvert(url, thumb)
                : null;
        }

        var first = listenUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        // Kestrel accepts "*" and "+" for "any address"; neither parses as a URI host.
        const string any = "any-host.invalid";
        var parseable = first.Replace("://*:", $"://{any}:").Replace("://+:", $"://{any}:");
        if (!Uri.TryCreate(parseable, UriKind.Absolute, out var uri)) return null;
        if (ConsoleAuth.IsLocalUrl(first)) return null;   // localhost only: no other PC can dial it

        var host = uri.Host;
        if (host == any || host is "0.0.0.0" or "::" or "[::]")
        {
            host = hostName ?? Dns.GetHostName();
            // macOS reports its short name; Windows PCs find it on the network as <name>.local.
            if (!host.Contains('.') && (isMac ?? OperatingSystem.IsMacOS())) host += ".local";
        }
        var scheme = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        return new RelayAdvert($"{scheme}://{host}:{uri.Port}", thumb);
    }
}
