using Orchestrator.Service.Services;
using Xunit;

namespace Orchestrator.Service.Tests;

public sealed class ChecksumServiceTests
{
    private readonly ChecksumService _svc = new();

    [Fact]
    public void ComputeSha256_MatchesKnownVector_UpperHex()
    {
        // SHA256("abc")
        var hash = _svc.ComputeSha256("abc"u8.ToArray());
        Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", hash);
    }

    [Fact]
    public void Verify_IsCaseInsensitive()
    {
        var data = "abc"u8.ToArray();
        var lower = _svc.ComputeSha256(data).ToLowerInvariant();
        Assert.True(_svc.Verify(data, lower));
    }

    [Fact]
    public void Verify_Fails_OnMismatch()
    {
        Assert.False(_svc.Verify("abc"u8.ToArray(), "DEADBEEF"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Verify_Passes_WhenNoExpectedChecksum(string? expected)
    {
        Assert.True(_svc.Verify("abc"u8.ToArray(), expected));
    }
}
