// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Gives this machine a private signing key that proves "I really am machine X" when it
//   dials the console's relay for a remote-control session.
//
//   Why it's needed: the session id travels through commands.json in the control repo,
//   which EVERY machine in the fleet can read. If that id were the whole credential, any
//   one machine could read another machine's pending session id, connect to the relay
//   first, and receive the operator's mouse and keystrokes meant for someone else.
//
//   How it works:
//     - On first use the service (running as SYSTEM) creates an ECDSA P-256 key pair and
//       keeps the private half in <root>\cache\relay-key.pem, readable only by SYSTEM and
//       Administrators. The public half goes out in every heartbeat.
//     - The console pins the first public key it sees for each machine (on the console's
//       own disk, not in git, where other machines could rewrite it).
//     - When a session is requested, the SERVICE signs "machine id + session id" and hands
//       that signature to the session process it launches. The session process runs as the
//       logged-on user and never sees the key itself; it only gets a signature that is
//       worthless for any other machine or any other session.
//     - The relay refuses an agent whose signature doesn't verify against the pinned key.
// =====================================================================================

using System.Runtime.Versioning;              // [SupportedOSPlatform]
using System.Security.AccessControl;          // FileSystemRights / FileSecurity
using System.Security.Cryptography;           // ECDsa
using System.Security.Principal;              // well-known SIDs (SYSTEM, Administrators)
using System.Text;                            // Encoding.UTF8
using Microsoft.Extensions.Logging;           // logging
using Orchestrator.Service.Models;            // OrchestratorConfig

namespace Orchestrator.Service.Services;

/// <summary>The wire format both ends agree on. The console has its own copy of this (see
/// RelaySignature.cs there); a test signs here and verifies there, so they can't drift apart.</summary>
public static class RelayProof
{
    /// <summary>The HTTP header the agent presents the signature in.</summary>
    public const string HeaderName = "X-Orchestrator-Relay-Proof";

    /// <summary>What gets signed. Versioned and newline-separated so no two (machine, session)
    /// pairs can ever produce the same bytes; the machine id is lowercased because the relay
    /// compares machine ids case-insensitively.</summary>
    public static byte[] Message(string machineId, string sessionId)
        => Encoding.UTF8.GetBytes($"orchestrator-relay-v1\n{machineId.Trim().ToLowerInvariant()}\n{sessionId.Trim()}");

    /// <summary>Session ids come from commands.json, i.e. from the repo, and end up on a
    /// scheduled task's command line. Accept only the shape the console actually generates
    /// (a GUID, with or without dashes) so nothing else can ever reach that command line.</summary>
    public static bool IsWellFormedSessionId(string? sessionId)
        => !string.IsNullOrEmpty(sessionId) && sessionId.Length <= 64
           && sessionId.All(c => char.IsAsciiHexDigit(c) || c == '-');
}

public interface IRelayIdentity
{
    /// <summary>This machine's public key (base64 SubjectPublicKeyInfo) for the heartbeat, or
    /// null when the key can't be loaded or created (the heartbeat then just omits it).</summary>
    string? PublicKey { get; }

    /// <summary>Sign a session for this machine. Returns lowercase hex (hex, not base64, because
    /// it travels as a command-line argument, and base64's '/' and '=' mean things there), or
    /// null if there's no usable key.</summary>
    string? Sign(string machineId, string sessionId);
}

public sealed class RelayIdentity : IRelayIdentity
{
    private readonly string _keyPath;
    private readonly ILogger<RelayIdentity> _log;
    private readonly Lazy<ECDsa?> _key;   // loaded/created once, on first use

    public RelayIdentity(IConfigService configService, ILogger<RelayIdentity> log)
        : this(Path.Combine(configService.Config.CachePath, "relay-key.pem"), log) { }

    /// <summary>Test seam: use a key file at an explicit path.</summary>
    public RelayIdentity(string keyPath, ILogger<RelayIdentity> log)
    {
        _keyPath = keyPath;
        _log = log;
        _key = new Lazy<ECDsa?>(LoadOrCreate);
    }

    public string? PublicKey
        => _key.Value is { } key ? Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) : null;

    public string? Sign(string machineId, string sessionId)
    {
        if (_key.Value is not { } key) return null;
        var sig = key.SignData(RelayProof.Message(machineId, sessionId), HashAlgorithmName.SHA256);
        return Convert.ToHexString(sig).ToLowerInvariant();
    }

    private ECDsa? LoadOrCreate()
    {
        try
        {
            if (File.Exists(_keyPath))
            {
                var existing = ECDsa.Create();
                existing.ImportFromPem(File.ReadAllText(_keyPath));
                return existing;
            }

            // First run (or the cache folder was wiped). A new key means the console will see
            // a changed key for this machine and refuse remote sessions until the operator
            // presses "Trust new key" — deliberately, since that's also what an impersonator
            // would look like.
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Directory.CreateDirectory(Path.GetDirectoryName(_keyPath)!);
            var tmp = _keyPath + ".tmp";
            File.WriteAllText(tmp, key.ExportECPrivateKeyPem());
            if (OperatingSystem.IsWindows()) RestrictToSystemAndAdmins(tmp);   // before it becomes the real file
            File.Move(tmp, _keyPath, overwrite: true);
            _log.LogInformation("relay identity: created a new remote-control signing key at {Path}", _keyPath);
            return key;
        }
        catch (Exception ex)
        {
            // Not fatal to anything but remote control: the heartbeat goes out without a key and
            // the console explains that this machine can't take sessions until it has one.
            _log.LogWarning(ex, "relay identity: could not load or create {Path}; remote control will be refused for this machine", _keyPath);
            return null;
        }
    }

    /// <summary>SYSTEM and Administrators only, nothing inherited. The install folder is already
    /// locked down the same way, but this file is the one that must never be readable by the
    /// logged-on user, so it doesn't rely on that.</summary>
    [SupportedOSPlatform("windows")]
    private static void RestrictToSystemAndAdmins(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null),
                FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }
}
