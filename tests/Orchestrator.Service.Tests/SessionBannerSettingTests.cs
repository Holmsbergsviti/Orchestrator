// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Checks that a PC draws nothing during a remote session unless the owner switched the
//   on-screen banner on.
// =====================================================================================

using Orchestrator.Service.Models;   // OrchestratorConfig
using Xunit;                         // the test framework

namespace Orchestrator.Service.Tests;

public class SessionBannerSettingTests
{
    [Fact]
    public void BannerIsOffByDefault()
        => Assert.False(new OrchestratorConfig().ShowSessionBanner);
}
