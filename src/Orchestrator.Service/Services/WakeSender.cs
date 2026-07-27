// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Sends a Wake-on-LAN "magic packet" to power on a machine by its MAC address. The
//   packet is a UDP broadcast (6x 0xFF followed by the target MAC repeated 16 times)
//   that a powered-off NIC listens for. Only reaches the local network segment, so the
//   sender (the "waker") must be an always-on machine on the same LAN as the targets.
// =====================================================================================

using System.Net;                     // IPAddress / IPEndPoint
using System.Net.Sockets;             // UdpClient
using Microsoft.Extensions.Logging;   // logging

namespace Orchestrator.Service.Services;

public static class WakeSender
{
    /// <summary>Broadcast a Wake-on-LAN magic packet to the given MAC. Returns true if sent.</summary>
    public static bool SendMagicPacket(string mac, ILogger log)
    {
        var target = ParseMac(mac);
        if (target is null)
        {
            log.LogWarning("wake: unrecognized MAC '{Mac}'", mac);
            return false;
        }

        try
        {
            var packet = new byte[102];                 // 6 sync bytes + 16 * 6 MAC bytes
            for (var i = 0; i < 6; i++) packet[i] = 0xFF;
            for (var i = 6; i < packet.Length; i += 6) Array.Copy(target, 0, packet, i, 6);

            using var udp = new UdpClient { EnableBroadcast = true };
            var endpoint = new IPEndPoint(IPAddress.Broadcast, 9);   // discard/WoL port; broadcast on the local segment
            udp.Send(packet, packet.Length, endpoint);
            udp.Send(packet, packet.Length, endpoint);               // twice, cheap insurance
            log.LogInformation("wake: sent magic packet to {Mac}", mac);
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "wake: failed to send magic packet to {Mac}", mac);
            return false;
        }
    }

    /// <summary>Parse "AA:BB:CC:DD:EE:FF" / "aabbccddeeff" / dashed forms into 6 bytes. Null if invalid.</summary>
    private static byte[]? ParseMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return null;
        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12) return null;
        var bytes = new byte[6];
        for (var i = 0; i < 6; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }
}
