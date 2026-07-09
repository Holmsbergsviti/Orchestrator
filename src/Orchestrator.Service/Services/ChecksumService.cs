using System.Security.Cryptography;

namespace Orchestrator.Service.Services;

public interface IChecksumService
{
    /// <summary>Compute the SHA256 of a byte buffer, upper-invariant hex, no prefix.</summary>
    string ComputeSha256(byte[] data);

    /// <summary>Compute the SHA256 of a file on disk.</summary>
    Task<string> ComputeSha256Async(string filePath, CancellationToken ct = default);

    /// <summary>Case-insensitive compare of a computed hash against an expected (normalized) hash.</summary>
    bool Verify(byte[] data, string? expectedNormalized);
}

public sealed class ChecksumService : IChecksumService
{
    public string ComputeSha256(byte[] data)
        => Convert.ToHexString(SHA256.HashData(data)); // already upper-case hex

    public async Task<string> ComputeSha256Async(string filePath, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash);
    }

    public bool Verify(byte[] data, string? expectedNormalized)
    {
        // No expected checksum => nothing to verify against. Treat as pass but caller should warn.
        if (string.IsNullOrWhiteSpace(expectedNormalized)) return true;
        var actual = ComputeSha256(data);
        return string.Equals(actual, expectedNormalized, StringComparison.OrdinalIgnoreCase);
    }
}
