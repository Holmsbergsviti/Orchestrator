// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Checks the one pure part of the launcher that starts screenshots and remote sessions on
//   the logged-in user's desktop: how the command line is put together. The Windows calls
//   themselves (tokens, sessions) can only be exercised on a real Windows PC.
// =====================================================================================

using Orchestrator.Service.Services;   // UserSessionLauncher
using Xunit;                           // the test framework

namespace Orchestrator.Service.Tests;

public class UserSessionLauncherTests
{
    [Fact]
    public void CommandLine_QuotesTheExe_AndKeepsArgumentsAsGiven()
    {
        Assert.Equal("\"C:\\Windows\\Orch\\orchestrator-service.exe\" remote-session abc123 - wss://x.trycloudflare.com -",
            UserSessionLauncher.BuildCommandLine("C:\\Windows\\Orch\\orchestrator-service.exe", "remote-session abc123 - wss://x.trycloudflare.com -"));
    }

    [Fact]
    public void CommandLine_WithPathContainingSpaces_StaysOneProgram()
    {
        Assert.StartsWith("\"C:\\Program Files\\Orch\\svc.exe\" capture-screenshot",
            UserSessionLauncher.BuildCommandLine("C:\\Program Files\\Orch\\svc.exe", "capture-screenshot r1"));
    }

    [Fact]
    public void CommandLine_WithNoArguments_IsJustTheQuotedExe()
    {
        Assert.Equal("\"C:\\a.exe\"", UserSessionLauncher.BuildCommandLine("C:\\a.exe", ""));
    }

    [Fact]
    public void WithSomeoneSignedIn_StartsOnTheirNormalDesktop()
        => Assert.Equal("winsta0\\default", UserSessionLauncher.DesktopFor(userSignedIn: true));

    [Fact]
    public void AtTheLoginScreen_StartsOnTheProtectedLoginDesktop()
        => Assert.Equal("winsta0\\winlogon", UserSessionLauncher.DesktopFor(userSignedIn: false));
}
