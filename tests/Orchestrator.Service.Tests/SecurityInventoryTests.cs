// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Automated checks for the pure, OS-independent parts of SecurityInventory: parsing
//   netsh's firewall-state output into "Profile: ON/OFF" lines. The WMI/registry lookups
//   themselves need real Windows and are not exercised here (same reasoning as
//   GetAntivirusNames, which is why only Normalize is unit-tested for that one).
// =====================================================================================

using Orchestrator.Service.Services;
using Xunit;

namespace Orchestrator.Service.Tests;

public sealed class SecurityInventoryTests
{
    [Fact]
    public void ParseFirewallState_ReadsAllThreeProfiles()
    {
        var output =
            "Domain Profile Settings:\n" +
            "----------------------------------------------------------------------\n" +
            "State                                 ON\n" +
            "\n" +
            "Private Profile Settings:\n" +
            "----------------------------------------------------------------------\n" +
            "State                                 ON\n" +
            "\n" +
            "Public Profile Settings:\n" +
            "----------------------------------------------------------------------\n" +
            "State                                 OFF\n" +
            "\n" +
            "Ok.\n";

        Assert.Equal(
            new[] { "Domain: ON", "Private: ON", "Public: OFF" },
            SecurityInventory.ParseFirewallState(output));
    }

    [Fact]
    public void ParseFirewallState_HandlesCrlfLineEndings()
    {
        var output = "Domain Profile Settings:\r\n----\r\nState ON\r\n";
        Assert.Equal(new[] { "Domain: ON" }, SecurityInventory.ParseFirewallState(output));
    }

    [Fact]
    public void ParseFirewallState_EmptyOrUnrecognizedInput_ReturnsEmpty()
    {
        Assert.Empty(SecurityInventory.ParseFirewallState(""));
        Assert.Empty(SecurityInventory.ParseFirewallState("garbage that isn't netsh output at all"));
    }

    [Fact]
    public void ParseFirewallState_IgnoresALineNotFollowingAProfileHeader()
    {
        // A "State" line before any "... Profile Settings:" header is noise, not data.
        var output = "State ON\nDomain Profile Settings:\n----\nState OFF\n";
        Assert.Equal(new[] { "Domain: OFF" }, SecurityInventory.ParseFirewallState(output));
    }
}
