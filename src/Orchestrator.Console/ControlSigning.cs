// =====================================================================================
// FILE PURPOSE (in plain terms):
//   The console's half of signed control files. It holds the operator's private signing
//   key (on this computer only — never in git) and writes a .sig file next to
//   manifest.json and commands.json every time the console saves them. Agents built with
//   the matching public key refuse those files without a valid signature, so a GitHub token
//   copied off one fleet machine can no longer make the fleet run anything.
//
//   The same key, stored as the AGENT_SIGNING_KEY secret in the code repo, lets the publish
//   workflow sign agent.json and bake the public key into every agent build.
//
//   The signed format must match ControlSignature in the service exactly; a unit test signs
//   here and verifies there (and checks an openssl-made signature) so they can't drift.
// =====================================================================================

using System.Security.Cryptography;   // ECDsa / SHA256
using System.Text;                    // Encoding.UTF8
using System.Text.Json;               // .sig files
using System.Text.Json.Serialization; // [JsonPropertyName]

namespace Orchestrator.Console;

/// <summary>How a control file on main stands against the operator's key.</summary>
public enum SignatureState
{
    /// <summary>Signed by this console's key, and unchanged since.</summary>
    Valid,
    /// <summary>No .sig file at all — e.g. never signed, or written before signing existed.</summary>
    Missing,
    /// <summary>A .sig exists but doesn't match: edited after signing, or signed by another key.</summary>
    Invalid
}

public sealed class ControlSigning
{
    public const string Version = "orchestrator-sign-v1";

    private sealed class SignatureFile
    {
        [JsonPropertyName("signedUtc")] public string SignedUtc { get; set; } = "";
        [JsonPropertyName("signature")] public string Signature { get; set; } = "";
    }

    private readonly string _keyPath;
    private readonly Lazy<ECDsa?> _key;

    public ControlSigning(ConsoleOptions opt)
        : this(string.IsNullOrWhiteSpace(opt.SigningKeyPath) ? DefaultKeyPath : opt.SigningKeyPath) { }

    /// <summary>Test seam / explicit location.</summary>
    public ControlSigning(string keyPath)
    {
        _keyPath = keyPath;
        _key = new Lazy<ECDsa?>(() =>
        {
            if (!File.Exists(_keyPath)) return null;
            var k = ECDsa.Create();
            k.ImportFromPem(File.ReadAllText(_keyPath));
            return k;
        });
    }

    /// <summary>Per-user app data, so it never lands in the control repo or this code repo.</summary>
    public static string DefaultKeyPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "OrchestratorConsole", "signing-key.pem");

    public string KeyPath => _keyPath;
    public bool HasKey => _key.Value is not null;

    /// <summary>The public key (base64 SubjectPublicKeyInfo), or null without a key.</summary>
    public string? PublicKeyBase64 => _key.Value is { } k ? Convert.ToBase64String(k.ExportSubjectPublicKeyInfo()) : null;

    /// <summary>Create the key if there isn't one. Returns (private key PEM, created?). The PEM
    /// is what goes into the AGENT_SIGNING_KEY secret.</summary>
    public (string Pem, bool Created) LoadOrCreate()
    {
        if (File.Exists(_keyPath)) return (File.ReadAllText(_keyPath), false);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportECPrivateKeyPem();   // "BEGIN EC PRIVATE KEY": what openssl in CI reads
        Directory.CreateDirectory(Path.GetDirectoryName(_keyPath)!);
        File.WriteAllText(_keyPath, pem);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);   // owner-only, like an ssh key
        return (pem, true);
    }

    /// <summary>The bytes that get signed (must match ControlSignature.Message in the service).</summary>
    public static byte[] Message(string path, string signedUtc, byte[] content)
        => Encoding.UTF8.GetBytes($"{Version}\n{path}\n{signedUtc}\n{Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()}");

    /// <summary>Sign <paramref name="content"/> as the file at <paramref name="path"/>. The time
    /// stamped in is never earlier than the previous signature's: agents refuse anything older
    /// than what they've already accepted, so a console whose clock moved backwards must not
    /// lock the fleet out of its own changes.</summary>
    public byte[] Sign(string path, byte[] content, byte[]? previousSignatureFile)
    {
        var key = _key.Value ?? throw new InvalidOperationException("No signing key.");
        // Whole seconds, because that's what gets written: compared at that precision, "later
        // than the previous one" has to mean a strictly later stamp, not the same second.
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        if (TryRead(previousSignatureFile, out var prev) && DateTimeOffset.TryParse(prev!.SignedUtc, out var prevAt) && prevAt >= now)
            now = prevAt.AddSeconds(1);
        var signedUtc = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var sig = key.SignData(Message(path, signedUtc, content), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        var file = new SignatureFile { SignedUtc = signedUtc, Signature = Convert.ToBase64String(sig) };
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    /// <summary>Is this content validly signed by OUR key?</summary>
    public SignatureState Check(string path, byte[] content, byte[]? signatureFile)
    {
        if (signatureFile is null) return SignatureState.Missing;
        if (_key.Value is not { } key || !TryRead(signatureFile, out var sig)) return SignatureState.Invalid;
        try
        {
            return key.VerifyData(Message(path, sig!.SignedUtc, content), Convert.FromBase64String(sig.Signature),
                HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)
                ? SignatureState.Valid
                : SignatureState.Invalid;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException) { return SignatureState.Invalid; }
    }

    private static bool TryRead(byte[]? bytes, out SignatureFile? sig)
    {
        sig = null;
        if (bytes is null) return false;
        try { sig = JsonSerializer.Deserialize<SignatureFile>(bytes); }
        catch (JsonException) { return false; }
        return sig is not null && !string.IsNullOrEmpty(sig.SignedUtc) && !string.IsNullOrEmpty(sig.Signature);
    }
}
