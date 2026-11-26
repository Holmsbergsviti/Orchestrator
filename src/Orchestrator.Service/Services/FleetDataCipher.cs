// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Encrypts everything an agent pushes to the fleet-state branch (heartbeat JSON, the log
//   tail, screenshot JPEGs) so that only the operator's own console — never GitHub, never
//   anyone who reads the private control repo, never a token copied off a machine — can read
//   it. This is one-way: an agent can SEAL data for the console with the console's public
//   key, but has no way to OPEN anything, including its own past uploads.
//
//   It's a small ECIES: a random one-time EC key is made for every message and combined with
//   the console's standing public key (Diffie-Hellman) to get a shared secret, which HKDF
//   turns into an AES-256-GCM key. The one-time public key travels alongside the ciphertext —
//   that's the only way the console can redo the same Diffie-Hellman and land on the same
//   key — but it being public teaches an eavesdropper nothing on its own.
//
//   The console has a byte-identical copy of this class (FleetDataCipher in
//   Orchestrator.Console); a unit test seals with one copy and opens with the other so they
//   can't drift apart. Compare with RelayCipher, which encrypts a LIVE session with a key
//   handed out per-session: this instead has one standing keypair (DataEncryptionKeys on the
//   console) that every agent build ever published encrypts to.
//
//   Data from an agent built before this existed, or from a build with no public key baked
//   in, has no magic prefix, so IsEncrypted says no and callers fall back to treating it as
//   plain text/bytes — old and new agents coexist on the same fleet with no flag day.
// =====================================================================================

using System.Security.Cryptography;   // ECDiffieHellman / AesGcm / HKDF
using System.Text;                    // Encoding.ASCII / UTF8

namespace Orchestrator.Service.Services;   // byte-identical copy: Orchestrator.Console.FleetDataCipher

public static class FleetDataCipher
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ORCD1");   // 5 bytes
    private const int PointBytes = 65;   // uncompressed P-256 point: 0x04 + 32-byte X + 32-byte Y
    private const int NonceBytes = 12;   // GCM nonce
    private const int TagBytes = 16;     // GCM authentication tag
    private const int KeyBytes = 32;     // AES-256

    /// <summary>True if <paramref name="data"/> looks like something this class produced — lets
    /// callers tell an encrypted upload from a plain one made by an older/unconfigured agent.</summary>
    public static bool IsEncrypted(byte[] data)
        => data.Length >= Magic.Length + PointBytes + NonceBytes + TagBytes
           && data.AsSpan(0, Magic.Length).SequenceEqual(Magic);

    /// <summary>Seal <paramref name="plaintext"/> so only the holder of the private half of
    /// <paramref name="recipientPublicKey"/> can read it.</summary>
    public static byte[] Encrypt(ECParameters recipientPublicKey, byte[] plaintext)
    {
        using var recipient = ECDiffieHellman.Create(recipientPublicKey);
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ephemeralPoint = EncodePoint(ephemeral.ExportParameters(false));

        var key = DeriveKey(ephemeral, recipient.PublicKey, ephemeralPoint);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[TagBytes];
        using (var aes = new AesGcm(key, TagBytes))
            aes.Encrypt(nonce, plaintext, cipher, tag);

        var wire = new byte[Magic.Length + PointBytes + NonceBytes + cipher.Length + TagBytes];
        var span = wire.AsSpan();
        Magic.CopyTo(span); span = span[Magic.Length..];
        ephemeralPoint.CopyTo(span); span = span[PointBytes..];
        nonce.CopyTo(span); span = span[NonceBytes..];
        cipher.CopyTo(span); span = span[cipher.Length..];
        tag.CopyTo(span);
        return wire;
    }

    /// <summary>Open a blob <see cref="Encrypt"/> made for this <paramref name="privateKey"/>. False
    /// for anything that isn't a genuine, untampered message to this key (wrong key, corrupted, or
    /// not one of ours at all) — never throws, so a caller can fall back safely.</summary>
    public static bool TryDecrypt(ECDiffieHellman privateKey, byte[] blob, out byte[] plaintext)
    {
        plaintext = Array.Empty<byte>();
        if (!IsEncrypted(blob)) return false;
        try
        {
            var span = blob.AsSpan(Magic.Length);
            var ephemeralPoint = span[..PointBytes].ToArray();
            var nonce = span.Slice(PointBytes, NonceBytes);
            var rest = span[(PointBytes + NonceBytes)..];
            var cipher = rest[..^TagBytes];
            var tag = rest[^TagBytes..];

            using var ephemeralPublic = ECDiffieHellman.Create(DecodePoint(ephemeralPoint));
            var key = DeriveKey(privateKey, ephemeralPublic.PublicKey, ephemeralPoint);

            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, cipher, tag, plain);
            plaintext = plain;
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    // Both sides derive the same key from (their own private key + the other side's public point),
    // bound to the one-time point so one message's key can't be reused to open another's.
    private static byte[] DeriveKey(ECDiffieHellman own, ECDiffieHellmanPublicKey other, byte[] ephemeralPoint)
    {
        var shared = own.DeriveRawSecretAgreement(other);
        var salt = Encoding.UTF8.GetBytes("orchestrator-fleet-data-v1");
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, KeyBytes, salt, ephemeralPoint);
    }

    private static byte[] EncodePoint(ECParameters p)
    {
        var point = new byte[PointBytes];
        point[0] = 0x04;
        p.Q.X!.CopyTo(point, 1);
        p.Q.Y!.CopyTo(point, 1 + 32);
        return point;
    }

    private static ECParameters DecodePoint(byte[] point)
    {
        if (point.Length != PointBytes || point[0] != 0x04) throw new CryptographicException("malformed point");
        return new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = point[1..33], Y = point[33..65] }
        };
    }
}
