// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Two small pieces that let the relay tell a real agent from an impersonator:
//
//   - RelaySignature checks the signature an agent presents when it connects for a
//     remote-control session. The agent's service signs "machine id + session id" with a
//     key only that machine holds (see RelayIdentity.cs in the service). This must build
//     the signed bytes EXACTLY as the agent does; a unit test signs with the agent's code
//     and verifies with this one so the two can't drift apart.
//
//   - RelayKeyStore remembers the first public key each machine reported ("pinning"), on
//     this computer's own disk. It deliberately does NOT live in the control repo: every
//     machine can write heartbeats there, so a key read fresh from git on every session
//     could be swapped by the very impersonator this is meant to stop. A machine whose key
//     later changes (reinstalled, cache wiped — or someone pretending to be it) is refused
//     until the operator presses "Trust new key".
// =====================================================================================

using System.Security.Cryptography;   // ECDsa
using System.Text;                    // Encoding.UTF8
using System.Text.Json;               // the pin file

namespace Orchestrator.Console;

public static class RelaySignature
{
    /// <summary>The header the agent sends its signature in (same constant as the agent's).</summary>
    public const string HeaderName = "X-Orchestrator-Relay-Proof";

    /// <summary>The signed bytes. Must match RelayProof.Message in the service exactly.</summary>
    public static byte[] Message(string machineId, string sessionId)
        => Encoding.UTF8.GetBytes($"orchestrator-relay-v1\n{machineId.Trim().ToLowerInvariant()}\n{sessionId.Trim()}");

    /// <summary>True only if <paramref name="proofHex"/> is a valid signature by the key in
    /// <paramref name="publicKeyBase64"/> over this machine + session. Anything malformed is
    /// simply false — this runs on unauthenticated input.</summary>
    public static bool Verify(string? publicKeyBase64, string machineId, string sessionId, string? proofHex)
    {
        if (string.IsNullOrWhiteSpace(publicKeyBase64) || string.IsNullOrWhiteSpace(proofHex)) return false;
        if (proofHex.Length > 512) return false;   // a P-256 signature is 128 hex chars; don't parse junk
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            return key.VerifyData(Message(machineId, sessionId), Convert.FromHexString(proofHex), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException) { return false; }
    }
}

/// <summary>How a machine's currently reported key compares with the one pinned for it.</summary>
public enum RelayKeyStatus
{
    /// <summary>Matches the pinned key (or was just pinned, being the first one seen).</summary>
    Trusted,
    /// <summary>The machine reports no key at all — an agent from before signing existed.</summary>
    Missing,
    /// <summary>Differs from the pinned key: a reinstall, or an impersonator. Refused until trusted.</summary>
    Changed
}

public sealed class RelayKeyStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private readonly ILogger<RelayKeyStore> _log;
    private Dictionary<string, string>? _pins;   // machine id -> pinned base64 public key

    public RelayKeyStore(ILogger<RelayKeyStore> log)
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
            "OrchestratorConsole", "relay-keys.json"), log) { }

    /// <summary>Test seam: keep the pins at an explicit path.</summary>
    public RelayKeyStore(string path, ILogger<RelayKeyStore> log)
    {
        _path = path;
        _log = log;
    }

    public string FilePath => _path;

    /// <summary>Compare a machine's reported key with its pin, pinning it if this is the first
    /// key ever seen for that machine. Called on every page load, so keys get pinned as early
    /// as possible — the window in which an impersonator could get pinned instead is the time
    /// between a machine first reporting a key and the console next being opened.</summary>
    public RelayKeyStatus Check(string machineId, string? reportedKey)
    {
        if (string.IsNullOrWhiteSpace(reportedKey)) return RelayKeyStatus.Missing;
        lock (_lock)
        {
            var pins = Load();
            if (pins.TryGetValue(machineId, out var pinned))
                return string.Equals(pinned, reportedKey, StringComparison.Ordinal) ? RelayKeyStatus.Trusted : RelayKeyStatus.Changed;
            pins[machineId] = reportedKey;
            Save(pins);
            _log.LogInformation("relay: pinned remote-control key for machine {MachineId}", machineId);
            return RelayKeyStatus.Trusted;
        }
    }

    /// <summary>The pinned key for a machine, or null if none.</summary>
    public string? Pinned(string machineId)
    {
        lock (_lock) return Load().TryGetValue(machineId, out var k) ? k : null;
    }

    /// <summary>The operator says the machine's new key is genuine (e.g. it was reinstalled).</summary>
    public void Trust(string machineId, string reportedKey)
    {
        lock (_lock)
        {
            var pins = Load();
            pins[machineId] = reportedKey;
            Save(pins);
        }
        _log.LogWarning("relay: operator re-trusted a CHANGED remote-control key for machine {MachineId}", machineId);
    }

    /// <summary>Forget a machine's pin, because the operator removed the machine: a reinstalled one is
    /// then pinned afresh the next time it reports, instead of needing "Trust new key".</summary>
    public void Unpin(string machineId)
    {
        lock (_lock)
        {
            var pins = Load();
            if (pins.Remove(machineId)) Save(pins);
        }
    }

    private Dictionary<string, string> Load()
    {
        if (_pins is not null) return _pins;
        try
        {
            _pins = File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? new()
                : new();
        }
        catch (Exception ex)
        {
            // Fail CLOSED on a corrupt file: starting over would silently re-pin whatever every
            // machine reports right now, impersonators included. Refuse until someone looks.
            throw new InvalidOperationException($"The remote-control key pins at {_path} are unreadable ({ex.Message}). " +
                "Fix or delete the file; deleting it re-pins every machine's current key.", ex);
        }
        _pins = new Dictionary<string, string>(_pins, StringComparer.OrdinalIgnoreCase);
        return _pins;
    }

    private void Save(Dictionary<string, string> pins)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(pins, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, overwrite: true);
    }
}
