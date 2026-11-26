// =====================================================================================
// FILE PURPOSE (in plain terms):
//   The console's half of encrypted fleet data (see FleetDataCipher). Holds the operator's
//   private decryption key on this computer only — never in git, never sent anywhere — and
//   decrypts whatever an agent sealed to the matching public key: heartbeats, log tails,
//   screenshots. The public half goes into the code repo's build (see docs/SETUP.md,
//   "Encrypt fleet data") so every agent build encrypts to it; nothing but this file, on
//   this computer, can ever read what comes back. Separate key from the operator's signing
//   key (ControlSigning) on purpose — one key, one job, so neither use can weaken the other.
// =====================================================================================

using System.Security.Cryptography;   // ECDiffieHellman
using Microsoft.Extensions.Logging;   // ILogger

namespace Orchestrator.Console;

public sealed class DataEncryptionKeys
{
    private readonly string _keyPath;
    private readonly Lazy<ECDiffieHellman?> _key;
    private readonly ILogger<DataEncryptionKeys>? _log;

    public DataEncryptionKeys(ConsoleOptions opt, ILogger<DataEncryptionKeys>? log = null)
        : this(string.IsNullOrWhiteSpace(opt.DataKeyPath) ? DefaultKeyPath : opt.DataKeyPath, log) { }

    /// <summary>Test seam / explicit location.</summary>
    public DataEncryptionKeys(string keyPath, ILogger<DataEncryptionKeys>? log = null)
    {
        _keyPath = keyPath;
        _log = log;
        _key = new Lazy<ECDiffieHellman?>(() =>
        {
            if (!File.Exists(_keyPath)) return null;
            var k = ECDiffieHellman.Create();
            k.ImportFromPem(File.ReadAllText(_keyPath));
            return k;
        });
    }

    /// <summary>Per-user app data, so it never lands in the control repo or this code repo.</summary>
    public static string DefaultKeyPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "OrchestratorConsole", "data-key.pem");

    public string KeyPath => _keyPath;
    public bool HasKey => _key.Value is not null;

    /// <summary>The public key (base64 SubjectPublicKeyInfo), or null without a key.</summary>
    public string? PublicKeyBase64 => _key.Value is { } k ? Convert.ToBase64String(k.ExportSubjectPublicKeyInfo()) : null;

    /// <summary>Create the key if there isn't one. Returns (private key PEM, created?). The PEM
    /// is what goes into the AGENT_DATA_PUBLIC_KEY build input (as its public half).</summary>
    public (string Pem, bool Created) LoadOrCreate()
    {
        if (File.Exists(_keyPath)) return (File.ReadAllText(_keyPath), false);
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportPkcs8PrivateKeyPem();   // never leaves this computer, so any standard PEM form is fine
        Directory.CreateDirectory(Path.GetDirectoryName(_keyPath)!);
        File.WriteAllText(_keyPath, pem);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);   // owner-only, like an ssh key
        return (pem, true);
    }

    /// <summary>Decrypt <paramref name="raw"/> if it looks like something FleetDataCipher made for
    /// this key; otherwise return it unchanged (an older agent, or a build with no key baked in,
    /// that uploaded plain bytes — the two coexist on one fleet). A blob that LOOKS encrypted but
    /// won't open with this key (wrong key, corrupted) logs a warning and comes back null —
    /// showing nothing is safer than showing garbage.</summary>
    public byte[]? DecryptIfNeeded(byte[]? raw, string what)
    {
        if (raw is null) return null;
        if (!FleetDataCipher.IsEncrypted(raw)) return raw;
        if (_key.Value is not { } key)
        {
            _log?.LogWarning("{What} is encrypted but no data key is set up on this console (run 'data-key'); skipping", what);
            return null;
        }
        if (FleetDataCipher.TryDecrypt(key, raw, out var plain)) return plain;
        _log?.LogWarning("{What} is encrypted but didn't open with this console's key", what);
        return null;
    }
}
