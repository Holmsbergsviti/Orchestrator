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

/// <summary>
/// Checks for the quarantine list that makes a rollback stick. Without it, a rejected build
/// gets reinstalled on the very next sync — the machine is back on the old binary, so the
/// published hash still doesn't match — and the crash/rollback cycle repeats forever across
/// the whole fleet.
/// </summary>
public sealed class FailedUpdatesTests
{
    private const string Bad = "b8e2e4e3e75bf0038afb6a4c7a69ad6ef8fddadabeda3db8629834fcbb59de65";
    private const string Good = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void RecognisesABuildItAlreadyRejected()
    {
        var q = new FailedUpdates { Failed = { new FailedUpdate { Sha256 = Bad } } };
        Assert.True(q.IsQuarantined(Bad));
        Assert.False(q.IsQuarantined(Good));
    }

    [Fact]
    public void MatchesRegardlessOfCasingOrFormatting()
    {
        // PowerShell writes this file and C# reads it. A casing difference silently
        // reintroducing the crash loop would be a miserable bug to track down.
        var q = new FailedUpdates { Failed = { new FailedUpdate { Sha256 = Bad.ToUpperInvariant() } } };
        Assert.True(q.IsQuarantined(Bad));
    }

    [Fact]
    public void EmptyOrMissingValuesQuarantineNothing()
    {
        var empty = new FailedUpdates();
        Assert.False(empty.IsQuarantined(Bad));
        Assert.False(empty.IsQuarantined(null));
        Assert.False(empty.IsQuarantined(""));

        // A junk entry must not accidentally match a real hash, or a corrupt file would
        // block every future update on that machine.
        var junk = new FailedUpdates { Failed = { new FailedUpdate { Sha256 = "" } } };
        Assert.False(junk.IsQuarantined(Bad));
    }

    [Fact]
    public void RoundTripsThroughTheJsonPowerShellWrites()
    {
        const string json = """
        { "failed": [ { "sha256": "B8E2E4E3E75BF0038AFB6A4C7A69AD6EF8FDDADABEDA3DB8629834FCBB59DE65",
                        "utc": "2026-08-04T13:00:00.000Z", "reason": "service is not running" } ] }
        """;
        var q = System.Text.Json.JsonSerializer.Deserialize<FailedUpdates>(json);
        Assert.NotNull(q);
        Assert.True(q!.IsQuarantined(Bad));
        Assert.Equal("service is not running", q.Failed[0].Reason);
    }
}
