// =====================================================================================
// FILE PURPOSE (in plain terms):
//   The "fingerprint" checker. It can compute a SHA256 hash of some bytes or a file,
//   and it can compare a computed hash against the one the manifest expected. This is
//   how the service proves a downloaded file is exactly what it should be (not
//   corrupted or swapped out) before installing it.
// =====================================================================================

using System.Security.Cryptography;   // provides the SHA256 hashing routines

namespace Orchestrator.Service.Services;   // groups this with the other services

public interface IChecksumService   // the contract other code depends on (makes it easy to swap/test)
{
    /// <summary>Compute the SHA256 of a byte buffer, upper-invariant hex, no prefix.</summary>
    string ComputeSha256(byte[] data);

    /// <summary>Compute the SHA256 of a file on disk.</summary>
    Task<string> ComputeSha256Async(string filePath, CancellationToken ct = default);

    /// <summary>Case-insensitive compare of a computed hash against an expected (normalized) hash.</summary>
    bool Verify(byte[] data, string? expectedNormalized);
}

public sealed class ChecksumService : IChecksumService   // the actual implementation
{
    public string ComputeSha256(byte[] data)
        => Convert.ToHexString(SHA256.HashData(data)); // hash the bytes and return upper-case hex text

    public async Task<string> ComputeSha256Async(string filePath, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(filePath);      // open the file for reading (auto-closed at the end)
        var hash = await SHA256.HashDataAsync(stream, ct);     // hash it while streaming (low memory use)
        return Convert.ToHexString(hash);                      // return the hash as upper-case hex text
    }

    public bool Verify(byte[] data, string? expectedNormalized)
    {
        // No expected checksum => nothing to verify against. Treat as pass but caller should warn.
        if (string.IsNullOrWhiteSpace(expectedNormalized)) return true;   // manifest had no checksum -> can't fail
        var actual = ComputeSha256(data);                                 // compute the real hash of the bytes
        return string.Equals(actual, expectedNormalized, StringComparison.OrdinalIgnoreCase);  // match? (ignore letter case)
    }
}
