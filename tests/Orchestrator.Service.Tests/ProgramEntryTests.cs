// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Automated checks for the two small helpers on a ProgramEntry: that the checksum
//   gets normalized (prefix stripped, upper-cased, whitespace trimmed, or null when
//   absent), and that the full file path is built by combining the install folder with
//   the file name.
// =====================================================================================

using Orchestrator.Service.Models;   // for ProgramEntry
using Xunit;                         // the test framework

namespace Orchestrator.Service.Tests;   // groups this with the other tests

public sealed class ProgramEntryTests
{
    [Theory]                                   // run once per row below (raw input -> expected output)
    [InlineData("sha256:abc123", "ABC123")]   // "sha256:" prefix stripped, upper-cased
    [InlineData("SHA256:AbC123", "ABC123")]   // prefix case doesn't matter
    [InlineData("  abc123  ", "ABC123")]      // surrounding whitespace trimmed
    [InlineData("abc123", "ABC123")]          // no prefix is fine too
    public void NormalizedChecksum_StripsPrefixAndUppercases(string raw, string expected)
    {
        var p = TestData.Program("a", checksum: raw);       // a program with the raw checksum
        Assert.Equal(expected, p.NormalizedChecksum);       // the normalized form should match
    }

    [Theory]                    // run once per row (each an "absent" value)
    [InlineData(null)]          // null
    [InlineData("")]            // empty
    [InlineData("   ")]         // whitespace only
    public void NormalizedChecksum_IsNull_WhenAbsent(string? raw)
    {
        var p = TestData.Program("a", checksum: raw);       // a program with no real checksum
        Assert.Null(p.NormalizedChecksum);                  // normalized form should be null
    }

    [Fact]
    public void FullFilePath_CombinesInstallPathAndFileName()
    {
        var p = TestData.Program("a", installPath: Path.Combine("root", "sub"), fileName: "tool.exe");  // folder + file
        Assert.Equal(Path.Combine("root", "sub", "tool.exe"), p.FullFilePath);                          // should be joined together
    }
}
