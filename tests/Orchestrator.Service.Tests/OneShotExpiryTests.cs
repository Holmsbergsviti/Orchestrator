// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Automated checks for when a one-shot request (restart, shutdown, screenshot, wake) is
//   too old to act on. A machine that was reinstalled forgets which requests it already
//   ran, so anything still sitting in commands.json must expire rather than replay.
// =====================================================================================

using Orchestrator.Service.Models;   // OneShotExpiry
using Xunit;                         // the test framework

namespace Orchestrator.Service.Tests;   // groups this with the other tests

public sealed class OneShotExpiryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);   // a fixed "now"
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);

    [Fact]
    public void UsesExpiresUtc_WhenPresent()
    {
        Assert.False(OneShotExpiry.IsExpired("2026-10-15T12:30:00Z", "2026-01-01T00:00:00Z", Day, Now));  // not yet
        Assert.True(OneShotExpiry.IsExpired("2026-10-15T11:59:00Z", "2026-10-15T11:58:00Z", Day, Now));   // just passed
    }

    [Fact]
    public void FallsBackToRequestedUtcPlusMaxAge_ForOlderConsoles()
    {
        Assert.False(OneShotExpiry.IsExpired(null, "2026-10-15T00:00:00Z", Day, Now));   // 12h old, allowed 24h
        Assert.True(OneShotExpiry.IsExpired(null, "2026-08-04T11:37:29Z", Day, Now));    // a months-old restart
    }

    [Fact]
    public void ReadsTheConsolesRoundTripFormat()
    {
        // The exact shape the console writes ("O" format with an offset).
        Assert.True(OneShotExpiry.IsExpired(null, "2026-08-04T11:37:29.4744540+00:00", Day, Now));
    }

    [Fact]
    public void TreatsUndatedRequestsAsCurrent()
    {
        Assert.False(OneShotExpiry.IsExpired(null, null, Day, Now));               // nothing to judge by
        Assert.False(OneShotExpiry.IsExpired("garbage", "also garbage", Day, Now));
    }
}
