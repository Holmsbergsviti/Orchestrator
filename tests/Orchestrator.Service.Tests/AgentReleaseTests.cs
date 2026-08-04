// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Automated checks for the agent self-update decision. This decides whether a machine
//   downloads and installs a new binary that will run as SYSTEM, so the tests care mostly
//   about the cases where it must say NO: a release with no checksum, a truncated or
//   malformed one, the kill switch, and anything that would leave a machine unable to tell
//   what it's running. Getting a "yes" wrong installs unverified code on every machine you own.
// =====================================================================================

using Orchestrator.Service.Models;     // the code being tested
using Orchestrator.Service.Services;   // ScheduledTaskService XML builder
using Xunit;                           // the test framework

namespace Orchestrator.Service.Tests;

public sealed class AgentReleaseTests
{
    private const string ShaA = "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90";
    private const string ShaB = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

    private static AgentRelease Release(string sha = ShaA, bool enabled = true, string url = "https://example.invalid/agent.exe")
        => new() { Sha256 = sha, ExeUrl = url, Enabled = enabled };

    [Fact]
    public void UpdatesWhenTheRunningBuildDiffers()
    {
        Assert.True(Release().ShouldUpdate(ShaB));
    }

    [Fact]
    public void DoesNotUpdateWhenAlreadyOnThePublishedBuild()
    {
        Assert.False(Release().ShouldUpdate(ShaA));
    }

    [Fact]
    public void ComparisonIgnoresCaseAndFormatting()
    {
        // Hashes get copied around by hand and by tooling; upper-case or colon-separated must
        // not read as "different build" and trigger a pointless fleet-wide reinstall.
        var release = Release(sha: ShaA.ToUpperInvariant());
        Assert.False(release.ShouldUpdate(ShaA));
        Assert.False(release.ShouldUpdate(ShaA.ToUpperInvariant()));
    }

    [Fact]
    public void KillSwitchStopsUpdates()
    {
        // enabled:false is the brake for when a bad build is already published.
        Assert.False(Release(enabled: false).ShouldUpdate(ShaB));
    }

    [Theory]
    [InlineData("")]                    // no checksum at all
    [InlineData("abc123")]              // too short to be a SHA-256
    [InlineData("not-a-hash-at-all")]   // not hex
    public void RefusesAReleaseItCannotVerify(string sha)
    {
        // No usable checksum means the download can't be proven to be the operator's build,
        // and an unverifiable binary must never be installed.
        var release = Release(sha: sha);
        Assert.False(release.IsUsable());
        Assert.False(release.ShouldUpdate(ShaB));
    }

    [Fact]
    public void RefusesAReleaseWithNowhereToDownloadFrom()
    {
        Assert.False(Release(url: "").IsUsable());
    }

    [Fact]
    public void RefusesToActWhenItCannotHashItself()
    {
        // If the agent can't hash its own exe it has no idea what it's running. Updating on a
        // guess could mean reinstalling the same build in a loop.
        var release = Release();
        Assert.False(release.ShouldUpdate(null));
        Assert.False(release.ShouldUpdate(""));
        Assert.False(release.ShouldUpdate("short"));
    }
}

public sealed class SystemTaskXmlTests
{
    [Fact]
    public void UpdateTask_RunsAsLocalSystem()
    {
        var xml = ScheduledTaskService.BuildSystemRunOnceXml(
            "Orchestrator self-update", "powershell.exe",
            "-ExecutionPolicy Bypass -NoProfile -File \"C:\\Windows\\Orch\\install.ps1\" -SourceDir \"C:\\Windows\\Orch\\update\"",
            new DateTime(2026, 8, 4, 12, 0, 0));

        // Swapping the service binary means stopping the service, which needs SYSTEM. By SID,
        // because "LocalSystem" is spelled differently on non-English Windows.
        Assert.Contains("<UserId>S-1-5-18</UserId>", xml);
        Assert.Contains("<RunLevel>HighestAvailable</RunLevel>", xml);
        Assert.DoesNotContain("InteractiveToken", xml);   // must not depend on anyone being logged in
        Assert.Contains("<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>", xml);   // never two updates at once
        Assert.Contains("2026-08-04T12:00:00", xml);
    }

    [Fact]
    public void UpdateTask_EscapesArgumentsAndIsWellFormed()
    {
        var xml = ScheduledTaskService.BuildSystemRunOnceXml(
            "Orchestrator self-update & verify", "powershell.exe",
            "-File \"C:\\a & b\\install.ps1\" -Note <test>",
            new DateTime(2026, 8, 4, 12, 0, 0));

        Assert.Contains("&amp;", xml);
        Assert.DoesNotContain("<test>", xml);

        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(xml);   // throws if the escaping produced malformed XML
        Assert.NotNull(doc.DocumentElement);
    }
}
