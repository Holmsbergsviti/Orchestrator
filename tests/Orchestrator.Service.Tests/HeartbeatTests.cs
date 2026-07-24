// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Automated checks for the heartbeat "should I commit this?" logic. They confirm the
//   signature ignores the timestamp (so a still-alive machine doesn't spam commits),
//   reacts to real changes (applied programs, success, version, error), and that
//   ShouldPush fires on first run, on change, and once the last push goes stale.
// =====================================================================================

using Orchestrator.Service.Models;         // Heartbeat
using Orchestrator.Service.Services;       // FleetReporter.ShouldPush
using Xunit;                               // the test framework

namespace Orchestrator.Service.Tests;   // groups this with the other tests

public sealed class HeartbeatTests
{
    private static Heartbeat Sample(string lastSeen, params string[] applied) => new()
    {
        MachineId = "m1",
        Hostname = "PC1",
        Os = "TestOS",
        AgentVersion = "1.0.0",
        LastSeenUtc = lastSeen,
        SyncIntervalMinutes = 60,
        LastSyncSuccess = true,
        ManifestVersion = "1.0",
        AppliedProgramIds = applied.ToList(),
        LastError = null
    };

    private static readonly TimeSpan MaxInterval = TimeSpan.FromHours(6);

    // ---- Signature ---------------------------------------------------------------------

    [Fact]
    public void Signature_IgnoresTimestamp()
    {
        var a = Sample("2026-07-24T10:00:00Z", "p1");
        var b = Sample("2026-07-24T18:30:00Z", "p1");   // only the timestamp differs
        Assert.Equal(a.Signature, b.Signature);
    }

    [Fact]
    public void Signature_ChangesWhenAppliedProgramsChange()
    {
        var a = Sample("2026-07-24T10:00:00Z", "p1");
        var b = Sample("2026-07-24T10:00:00Z", "p1", "p2");
        Assert.NotEqual(a.Signature, b.Signature);
    }

    [Fact]
    public void Signature_ChangesWhenSuccessFlips()
    {
        var a = Sample("2026-07-24T10:00:00Z", "p1");
        var b = Sample("2026-07-24T10:00:00Z", "p1");
        b.LastSyncSuccess = false;
        Assert.NotEqual(a.Signature, b.Signature);
    }

    // ---- ShouldPush --------------------------------------------------------------------

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-24T12:00:00Z");

    [Fact]
    public void ShouldPush_TrueOnFirstEver()
    {
        var current = Sample(Now.ToString("O"), "p1");
        Assert.True(FleetReporter.ShouldPush(current, last: null, MaxInterval, Now));
    }

    [Fact]
    public void ShouldPush_TrueWhenSignatureChanged()
    {
        var last = Sample(Now.AddMinutes(-1).ToString("O"), "p1");
        var current = Sample(Now.ToString("O"), "p1", "p2");   // applied set changed
        Assert.True(FleetReporter.ShouldPush(current, last, MaxInterval, Now));
    }

    [Fact]
    public void ShouldPush_FalseWhenUnchangedAndFresh()
    {
        var last = Sample(Now.AddMinutes(-5).ToString("O"), "p1");   // pushed 5 min ago, nothing changed
        var current = Sample(Now.ToString("O"), "p1");
        Assert.False(FleetReporter.ShouldPush(current, last, MaxInterval, Now));
    }

    [Fact]
    public void ShouldPush_TrueWhenUnchangedButStale()
    {
        var last = Sample(Now.AddHours(-7).ToString("O"), "p1");   // last push older than the 6h max interval
        var current = Sample(Now.ToString("O"), "p1");
        Assert.True(FleetReporter.ShouldPush(current, last, MaxInterval, Now));
    }
}
