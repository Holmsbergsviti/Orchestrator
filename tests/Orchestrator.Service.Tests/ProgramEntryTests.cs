using Orchestrator.Service.Models;
using Xunit;

namespace Orchestrator.Service.Tests;

public sealed class ProgramEntryTests
{
    [Theory]
    [InlineData("sha256:abc123", "ABC123")]
    [InlineData("SHA256:AbC123", "ABC123")]
    [InlineData("  abc123  ", "ABC123")]
    [InlineData("abc123", "ABC123")]
    public void NormalizedChecksum_StripsPrefixAndUppercases(string raw, string expected)
    {
        var p = TestData.Program("a", checksum: raw);
        Assert.Equal(expected, p.NormalizedChecksum);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizedChecksum_IsNull_WhenAbsent(string? raw)
    {
        var p = TestData.Program("a", checksum: raw);
        Assert.Null(p.NormalizedChecksum);
    }

    [Fact]
    public void FullFilePath_CombinesInstallPathAndFileName()
    {
        var p = TestData.Program("a", installPath: Path.Combine("root", "sub"), fileName: "tool.exe");
        Assert.Equal(Path.Combine("root", "sub", "tool.exe"), p.FullFilePath);
    }
}
