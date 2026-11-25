// =====================================================================================
// FILE PURPOSE (in plain terms):
//   End-to-end encryption for a live screen session, so that whoever carries the traffic
//   between this console and the PC (the Cloudflare tunnel, see TunnelService) only ever sees
//   scrambled bytes. It is used only when the operator's signed relay hint contains a per-session
//   key ("relayKey"); sessions without one run as before.
//
//   The key is 32 random bytes made by the console for ONE session and delivered to the PC through
//   the signed relay hint on GitHub — a path that never touches the tunnel. Each direction gets
//   its own key (derived from it with HKDF), every message is sealed with AES-256-GCM, and each
//   carries a counter that must go up by exactly one, so a message can't be altered, replayed,
//   dropped or reordered without the receiver noticing. The message kind (screen frame = binary,
//   input = text) travels inside the sealed part, so even that is hidden.
//
//   The agent has a copy of this class (RelayCipher in Orchestrator.Service); a unit test seals
//   with one and opens with the other so the two cannot drift apart. Keep them in step.
//
//   Not provided: forward secrecy. Anyone who later reads the hint commit from GitHub AND had
//   recorded the traffic could decrypt it; the hint branch is deleted as soon as the session
//   ends, and GitHub only keeps the orphaned commit for a while.
// =====================================================================================

using System.Buffers.Binary;            // the counter, big-endian
using System.Security.Cryptography;     // AesGcm, HKDF, RandomNumberGenerator
using System.Text;                      // Encoding.UTF8
using System.Text.RegularExpressions;   // the key's shape

namespace Orchestrator.Console;

public sealed partial class RelayCipher
{
    private const int KeyBytes = 32;       // AES-256
    private const int CounterBytes = 8;    // sent in the clear in front of each message
    private const int TagBytes = 16;       // GCM authentication tag
    private const int NonceBytes = 12;     // GCM nonce: 4 zero bytes + the counter

    // 32 bytes as unpadded URL-safe base64 is exactly 43 characters. It lands on a command line, so strict.
    [GeneratedRegex("^[A-Za-z0-9_-]{43}$")]
    private static partial Regex KeyPattern();

    public static bool IsWellFormedKey(string? key) => key is not null && KeyPattern().IsMatch(key);

    /// <summary>A fresh random session key, ready to put in the hint.</summary>
    public static string NewKey() => Base64UrlEncode(RandomNumberGenerator.GetBytes(KeyBytes));

    private readonly byte[] _sendKey;
    private readonly byte[] _receiveKey;
    private readonly object _gate = new();
    private ulong _sendCounter;       // the next counter to send
    private ulong _receiveCounter;    // the next counter we will accept

    /// <param name="key">The session key from the relay hint.</param>
    /// <param name="sessionId">Bound into both derived keys, so a key can't be reused for another session.</param>
    /// <param name="isAgent">True on the PC, false on the console: they send with each other's receive key.</param>
    public RelayCipher(string key, string sessionId, bool isAgent)
    {
        if (!IsWellFormedKey(key)) throw new ArgumentException("malformed relay key", nameof(key));
        byte[] master;
        try { master = Base64UrlDecode(key); }
        catch (FormatException) { throw new ArgumentException("malformed relay key", nameof(key)); }
        var salt = Encoding.UTF8.GetBytes("orchestrator-relay-e2e-v1");
        var toConsole = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, KeyBytes, salt, Encoding.UTF8.GetBytes($"agent->console|{sessionId}"));
        var toAgent = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, KeyBytes, salt, Encoding.UTF8.GetBytes($"console->agent|{sessionId}"));
        _sendKey = isAgent ? toConsole : toAgent;
        _receiveKey = isAgent ? toAgent : toConsole;
        CryptographicOperations.ZeroMemory(master);
    }

    /// <summary>Seal one message. The result is what goes on the wire as a single binary WebSocket message.</summary>
    public byte[] Seal(bool isText, ReadOnlySpan<byte> payload)
    {
        ulong counter;
        lock (_gate) counter = _sendCounter++;

        var plain = new byte[1 + payload.Length];
        plain[0] = isText ? (byte)1 : (byte)0;   // the kind is sealed along with the data
        payload.CopyTo(plain.AsSpan(1));

        var wire = new byte[CounterBytes + plain.Length + TagBytes];
        BinaryPrimitives.WriteUInt64BigEndian(wire, counter);
        using var aes = new AesGcm(_sendKey, TagBytes);
        aes.Encrypt(Nonce(counter), plain, wire.AsSpan(CounterBytes, plain.Length), wire.AsSpan(CounterBytes + plain.Length, TagBytes));
        return wire;
    }

    /// <summary>Open one message. Throws <see cref="CryptographicException"/> for anything that isn't
    /// the next genuine message from the other side: wrong key, altered, replayed, skipped or too short.</summary>
    public (bool IsText, byte[] Payload) Open(ReadOnlySpan<byte> wire)
    {
        if (wire.Length < CounterBytes + 1 + TagBytes) throw new CryptographicException("message too short");
        var counter = BinaryPrimitives.ReadUInt64BigEndian(wire);
        lock (_gate)
            if (counter != _receiveCounter) throw new CryptographicException("message out of sequence");

        var cipherLength = wire.Length - CounterBytes - TagBytes;
        var plain = new byte[cipherLength];
        using var aes = new AesGcm(_receiveKey, TagBytes);
        aes.Decrypt(Nonce(counter), wire.Slice(CounterBytes, cipherLength), wire.Slice(CounterBytes + cipherLength, TagBytes), plain);

        lock (_gate) _receiveCounter = counter + 1;   // only a message that authenticated moves the window
        return (plain[0] == 1, plain.AsSpan(1).ToArray());
    }

    private static byte[] Nonce(ulong counter)
    {
        var nonce = new byte[NonceBytes];
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), counter);
        return nonce;
    }

    private static string Base64UrlEncode(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string s)
        => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/') + "=");   // 43 chars always need exactly one '='
}
