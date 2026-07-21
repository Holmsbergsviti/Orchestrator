// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Automated checks for the "figure out the repo file path" logic. They confirm that
//   an explicit path wins (and gets its leading slash trimmed), that a raw GitHub URL
//   is parsed correctly (dropping owner/repo/branch), and that anything unusable
//   (nothing set, or a non-GitHub host) returns null.
// =====================================================================================

using Orchestrator.Service.Services;   // the code being tested
using Xunit;                           // the test framework

namespace Orchestrator.Service.Tests;   // groups this with the other tests

public sealed class GitHubClientTests
{
    [Fact]
    public void ResolveRepoPath_PrefersExplicitPath_AndTrimsLeadingSlash()
    {
        var p = TestData.Program("a");                                        // a sample program
        p.Path = "/programs/tool/v1/tool.exe";                               // it has an explicit path (with a leading slash)
        p.Url = "https://raw.githubusercontent.com/o/r/main/ignored.exe";    // and a url that should be ignored

        Assert.Equal("programs/tool/v1/tool.exe", GitHubClient.ResolveRepoPath(p));  // path wins, leading slash trimmed
    }

    [Fact]
    public void ResolveRepoPath_ParsesRawGithubUrl_DroppingOwnerRepoBranch()
    {
        var p = TestData.Program("a");                                       // a sample program
        p.Url = "https://raw.githubusercontent.com/octo/repo/main/programs/tool/v1/tool.exe";  // only a url, no path

        Assert.Equal("programs/tool/v1/tool.exe", GitHubClient.ResolveRepoPath(p));  // owner/repo/branch dropped, rest kept
    }

    [Fact]
    public void ResolveRepoPath_ReturnsNull_WhenNeitherPathNorUrl()
    {
        var p = TestData.Program("a");                          // neither path nor url set
        Assert.Null(GitHubClient.ResolveRepoPath(p));           // nothing to resolve -> null
    }

    [Fact]
    public void ResolveRepoPath_ReturnsNull_ForNonGithubHost()
    {
        var p = TestData.Program("a");                          // a sample program
        p.Url = "https://example.com/o/r/main/tool.exe";        // a url that isn't a GitHub raw url
        Assert.Null(GitHubClient.ResolveRepoPath(p));           // unrecognized host -> null
    }
}
