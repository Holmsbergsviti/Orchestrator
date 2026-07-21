// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Automated checks for the checksum (fingerprint) code. They confirm that hashing a
//   known input gives the known correct hash, that comparison ignores upper/lower case,
//   that a wrong hash is rejected, and that "no expected checksum" is treated as a pass.
// =====================================================================================

using Orchestrator.Service.Services;   // the code being tested
using Xunit;                           // the test framework

namespace Orchestrator.Service.Tests;   // groups this with the other tests

public sealed class ChecksumServiceTests
{
    private readonly ChecksumService _svc = new();   // a fresh service instance for each test

    [Fact]   // a single, no-parameters test
    public void ComputeSha256_MatchesKnownVector_UpperHex()
    {
        // SHA256("abc")
        var hash = _svc.ComputeSha256("abc"u8.ToArray());   // hash the bytes of "abc"
        Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", hash);  // must equal the known hash
    }

    [Fact]
    public void Verify_IsCaseInsensitive()
    {
        var data = "abc"u8.ToArray();                            // some bytes
        var lower = _svc.ComputeSha256(data).ToLowerInvariant(); // their hash, lower-cased
        Assert.True(_svc.Verify(data, lower));                   // verify should still pass despite the case difference
    }

    [Fact]
    public void Verify_Fails_OnMismatch()
    {
        Assert.False(_svc.Verify("abc"u8.ToArray(), "DEADBEEF"));   // a clearly wrong expected hash must fail
    }

    [Theory]                 // a test run once per row of data below
    [InlineData(null)]       // case 1: expected checksum is null
    [InlineData("")]         // case 2: expected checksum is empty
    public void Verify_Passes_WhenNoExpectedChecksum(string? expected)
    {
        Assert.True(_svc.Verify("abc"u8.ToArray(), expected));   // with nothing to compare against, verify passes
    }
}
