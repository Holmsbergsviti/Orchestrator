using Orchestrator.Service.Services;
using Xunit;

namespace Orchestrator.Service.Tests;

public sealed class GitHubClientTests
{
    [Fact]
    public void ResolveRepoPath_PrefersExplicitPath_AndTrimsLeadingSlash()
    {
        var p = TestData.Program("a");
        p.Path = "/programs/tool/v1/tool.exe";
        p.Url = "https://raw.githubusercontent.com/o/r/main/ignored.exe";

        Assert.Equal("programs/tool/v1/tool.exe", GitHubClient.ResolveRepoPath(p));
    }

    [Fact]
    public void ResolveRepoPath_ParsesRawGithubUrl_DroppingOwnerRepoBranch()
    {
        var p = TestData.Program("a");
        p.Url = "https://raw.githubusercontent.com/octo/repo/main/programs/tool/v1/tool.exe";

        Assert.Equal("programs/tool/v1/tool.exe", GitHubClient.ResolveRepoPath(p));
    }

    [Fact]
    public void ResolveRepoPath_ReturnsNull_WhenNeitherPathNorUrl()
    {
        var p = TestData.Program("a");
        Assert.Null(GitHubClient.ResolveRepoPath(p));
    }

    [Fact]
    public void ResolveRepoPath_ReturnsNull_ForNonGithubHost()
    {
        var p = TestData.Program("a");
        p.Url = "https://example.com/o/r/main/tool.exe";
        Assert.Null(GitHubClient.ResolveRepoPath(p));
    }
}
