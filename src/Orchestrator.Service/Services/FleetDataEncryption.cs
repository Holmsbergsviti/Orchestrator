// =====================================================================================
// FILE PURPOSE (in plain terms):
//   The agent's half of encrypted fleet data (see FleetDataCipher and the console's
//   DataEncryptionKeys). Holds only the operator's PUBLIC key, baked into the build by CI
//   the same way the control-file signing key is (see ControlFileVerifier) — so there is
//   nothing secret in this class, or anywhere in the exe, at all. A build with no key baked
//   in (local dev, or CI before the key is set) encrypts nothing and behaves exactly as
//   before: heartbeats, logs and screenshots go up in the clear, like today.
// =====================================================================================

using System.Reflection;              // AssemblyMetadataAttribute: where CI bakes in the public key
using System.Security.Cryptography;   // ECDiffieHellman

namespace Orchestrator.Service.Services;

public interface IFleetDataEncryption
{
    /// <summary>True when this build carries a public key — uploads are sealed rather than plain.</summary>
    bool Enabled { get; }

    /// <summary>Seal <paramref name="plaintext"/> for the console if this build has a key baked
    /// in; otherwise return it unchanged (pre-encryption behaviour).</summary>
    byte[] EncryptIfEnabled(byte[] plaintext);
}

public sealed class FleetDataEncryption : IFleetDataEncryption
{
    /// <summary>The assembly-metadata key CI stores the operator's public key under.</summary>
    public const string MetadataKey = "OrchestratorDataPublicKey";

    private readonly ECParameters? _publicKey;

    public FleetDataEncryption() : this(BakedInKey()) { }

    /// <summary>Test seam: an explicit key (base64 SubjectPublicKeyInfo), or null for "not enabled".</summary>
    public FleetDataEncryption(string? publicKeyBase64)
    {
        if (string.IsNullOrWhiteSpace(publicKeyBase64)) { _publicKey = null; return; }
        using var key = ECDiffieHellman.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64.Trim()), out _);
        _publicKey = key.ExportParameters(false);
    }

    public bool Enabled => _publicKey.HasValue;

    public byte[] EncryptIfEnabled(byte[] plaintext)
        => _publicKey is { } key ? FleetDataCipher.Encrypt(key, plaintext) : plaintext;

    private static string? BakedInKey()
        => typeof(FleetDataEncryption).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == MetadataKey)?.Value;
}
