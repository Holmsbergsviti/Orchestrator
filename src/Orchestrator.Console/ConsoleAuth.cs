// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Small auth helpers for the console: a constant-time token comparison (so checking a
//   wrong access token doesn't leak timing information about how much of it was right),
//   a check for whether a configured listen URL is localhost-only, and a tiny in-memory
//   lockout so repeated wrong-token attempts from one IP get throttled. None of this is
//   hand-rolled cryptography — the actual session is ASP.NET Core's built-in cookie auth
//   (see Program.cs); this file only backs the /login form itself.
// =====================================================================================

using System.Collections.Concurrent;   // ConcurrentDictionary for the lockout tracker
using System.Security.Cryptography;    // CryptographicOperations.FixedTimeEquals
using System.Text;                     // Encoding.UTF8

namespace Orchestrator.Console;

public static class ConsoleAuth
{
    /// <summary>Compare two strings without leaking timing information about a partial match.</summary>
    public static bool ConstantTimeEquals(string a, string b)
    {
        var ab = Encoding.UTF8.GetBytes(a ?? "");
        var bb = Encoding.UTF8.GetBytes(b ?? "");
        // A length mismatch alone reveals nothing about content, so it's fine to short-circuit —
        // FixedTimeEquals itself requires equal-length spans anyway.
        return ab.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ab, bb);
    }

    /// <summary>True only if every address in a (possibly ';'-separated) Urls value is loopback.
    /// Anything else — including "0.0.0.0" ("bind all interfaces") — counts as non-local.</summary>
    public static bool IsLocalUrl(string urls)
    {
        var parts = urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;
        foreach (var part in parts)
        {
            if (!Uri.TryCreate(part, UriKind.Absolute, out var uri)) return false;   // unparsable -> treat as non-local
            if (uri.Host is not ("localhost" or "127.0.0.1" or "::1")) return false;
        }
        return true;
    }
}

/// <summary>Throttles repeated failed /login attempts: N failures from one key (IP) within a
/// window locks that key out until the window passes. In-memory only — resets on restart,
/// which is fine for a single-operator local console.</summary>
public sealed class LoginLockout
{
    private const int MaxAttempts = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset WindowStart)> _attempts = new();

    public bool IsLocked(string key)
        => _attempts.TryGetValue(key, out var entry)
           && DateTimeOffset.UtcNow - entry.WindowStart <= Window
           && entry.Count >= MaxAttempts;

    public void RecordFailure(string key)
        => _attempts.AddOrUpdate(key,
            _ => (1, DateTimeOffset.UtcNow),
            (_, existing) => DateTimeOffset.UtcNow - existing.WindowStart > Window
                ? (1, DateTimeOffset.UtcNow)
                : (existing.Count + 1, existing.WindowStart));

    public void Reset(string key) => _attempts.TryRemove(key, out _);
}
