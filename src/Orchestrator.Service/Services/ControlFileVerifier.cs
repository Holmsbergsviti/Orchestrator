// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Makes sure the instructions this machine follows really came from the operator.
//
//   Every machine needs a GitHub token to read the control repo and write its heartbeat,
//   and on a private repo GitHub can't stop that token from also pushing to main. So one
//   token copied off one machine could rewrite manifest.json, commands.json or agent.json
//   and have every machine run something as SYSTEM.
//
//   The fix: the operator's console signs manifest.json and commands.json, and the publish
//   workflow signs agent.json, each with the same private key. Every agent build carries
//   the matching PUBLIC key (baked in by CI from the same secret), and this class refuses
//   any of those files whose signature is missing, wrong, or older than one already
//   accepted (so an old, genuinely signed file can't be replayed to undo a change).
//   Program binaries are covered through the signed manifest: their SHA-256 is in it, and
//   while signing is on, a program without one is refused.
//
//   A build with no key baked in (a local dev build, or CI before the secret exists)
//   doesn't enforce anything and behaves exactly as before.
//
//   Signature files sit next to what they sign — manifest.json.sig — and look like:
//     { "signedUtc": "2026-10-29T20:15:00Z", "signature": "<base64 DER ECDSA P-256/SHA-256>" }
//   over the bytes of:  orchestrator-sign-v1\n<path>\n<signedUtc>\n<sha256 of the file, hex>
//   The console (ControlSigning.cs) and publish-agent.yml (openssl) produce exactly that.
// =====================================================================================

using System.Reflection;              // AssemblyMetadataAttribute: where CI bakes in the public key
using System.Security.Cryptography;   // ECDsa / SHA256
using System.Text;                    // Encoding.UTF8
using System.Text.Json;               // the .sig files and the replay-protection state
using System.Text.Json.Serialization; // [JsonPropertyName]
using Microsoft.Extensions.Logging;   // logging

namespace Orchestrator.Service.Services;

/// <summary>The wire format, shared (by copy) with the console. A test checks both sides
/// and an openssl-made signature against each other so they can't drift apart.</summary>
public static class ControlSignature
{
    public const string Version = "orchestrator-sign-v1";

    /// <summary>The assembly-metadata key CI stores the trusted public key under.</summary>
    public const string MetadataKey = "OrchestratorSigningKey";

    /// <summary>The bytes that get signed. Binding the repo path in means a signature for
    /// commands.json can't be passed off as one for manifest.json.</summary>
    public static byte[] Message(string path, string signedUtc, byte[] content)
        => Encoding.UTF8.GetBytes($"{Version}\n{path}\n{signedUtc}\n{Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()}");

    /// <summary>Where a file's signature lives in the repo.</summary>
    public static string SignaturePath(string path) => path + ".sig";
}

/// <summary>The shape of a .sig file.</summary>
public sealed class SignatureFile
{
    [JsonPropertyName("signedUtc")] public string SignedUtc { get; set; } = "";
    [JsonPropertyName("signature")] public string Signature { get; set; } = "";
}

public interface IControlFileVerifier
{
    /// <summary>True when this build carries a trusted key, i.e. unsigned files are refused.</summary>
    bool Enforced { get; }

    /// <summary>Check <paramref name="content"/> (the file at <paramref name="path"/>) against
    /// its signature file. Returns null when the file may be used, or a one-line reason why
    /// not. Always null when <see cref="Enforced"/> is false.</summary>
    string? Check(string path, byte[] content, byte[]? signatureFile);
}

public sealed class ControlFileVerifier : IControlFileVerifier
{
    private readonly byte[]? _publicKey;   // SubjectPublicKeyInfo DER; null = not enforcing
    private readonly string _statePath;    // remembers the newest signedUtc accepted per file
    private readonly ILogger<ControlFileVerifier> _log;
    private readonly object _lock = new();

    public ControlFileVerifier(IConfigService configService, ILogger<ControlFileVerifier> log)
        : this(BakedInKey(), Path.Combine(configService.Config.CachePath, "signature-state.json"), log) { }

    /// <summary>Test seam: an explicit key (base64 SPKI, or null for "not enforcing").</summary>
    public ControlFileVerifier(string? publicKeyBase64, string statePath, ILogger<ControlFileVerifier> log)
    {
        _publicKey = string.IsNullOrWhiteSpace(publicKeyBase64) ? null : Convert.FromBase64String(publicKeyBase64.Trim());
        _statePath = statePath;
        _log = log;
    }

    public bool Enforced => _publicKey is not null;

    private static string? BakedInKey()
        => typeof(ControlFileVerifier).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == ControlSignature.MetadataKey)?.Value;

    public string? Check(string path, byte[] content, byte[]? signatureFile)
    {
        if (_publicKey is null) return null;   // this build doesn't enforce signing
        if (signatureFile is null)
            return $"{path} isn't signed. Open the console and press Sign (only if you made the change).";

        SignatureFile? sig;
        try { sig = JsonSerializer.Deserialize<SignatureFile>(signatureFile); }
        catch (JsonException) { sig = null; }
        if (sig is null || !DateTimeOffset.TryParse(sig.SignedUtc, out var signedAt))
            return $"{ControlSignature.SignaturePath(path)} is unreadable.";

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(_publicKey, out _);
            var ok = key.VerifyData(ControlSignature.Message(path, sig.SignedUtc, content),
                Convert.FromBase64String(sig.Signature), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            if (!ok)
                return $"{path} doesn't match its signature: it was changed after it was signed, or signed with another key.";
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return $"{ControlSignature.SignaturePath(path)} is malformed.";
        }

        // Replay protection: a genuine but OLDER signed file (say, the manifest from before a
        // program was removed) must not be accepted after a newer one was.
        lock (_lock)
        {
            var state = LoadState();
            if (state.TryGetValue(path, out var newest) && signedAt < newest)
                return $"{path} is signed {signedAt:u}, older than the {newest:u} version already applied here. Refusing what looks like a replay.";
            if (!state.TryGetValue(path, out newest) || signedAt > newest)
            {
                state[path] = signedAt;
                SaveState(state);
            }
        }
        return null;
    }

    private Dictionary<string, DateTimeOffset> LoadState()
    {
        try
        {
            return File.Exists(_statePath)
                ? JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(_statePath)) ?? new()
                : new();
        }
        catch (Exception ex)
        {
            // Losing this only weakens replay protection, it never blocks a genuine file.
            _log.LogWarning(ex, "signature state unreadable; starting over");
            return new();
        }
    }

    private void SaveState(Dictionary<string, DateTimeOffset> state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            File.WriteAllText(_statePath, JsonSerializer.Serialize(state));
        }
        catch (Exception ex)
        {
            // e.g. the run-program verb, which may lack write access to the cache folder.
            _log.LogDebug(ex, "could not save signature state");
        }
    }
}
