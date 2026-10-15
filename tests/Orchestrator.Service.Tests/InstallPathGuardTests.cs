// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Automated checks for the "is this folder safe to delete?" rule used when a program is
//   uninstalled. It must allow an ordinary per-program folder, and refuse drive roots,
//   system and user-profile folders (and their parents), the agent's own folders, relative
//   or network paths, and any folder another active program still lives in.
// =====================================================================================

using Orchestrator.Service.Services;   // InstallPathGuard
using Xunit;                           // the test framework

namespace Orchestrator.Service.Tests;   // groups this with the other tests

public sealed class InstallPathGuardTests
{
    private const string AgentRoot = @"C:\Windows\Orch";              // the default install root
    private static readonly string[] NoneInUse = Array.Empty<string>();   // no other programs installed

    [Theory]
    [InlineData(@"C:\Windows\Orch\programs\my-app")]   // the console's default layout
    [InlineData(@"C:\Windows\Orch\programs\my-app\")]  // trailing slash is fine
    [InlineData(@"C:\Tools\my-app")]                   // a folder of the operator's choosing
    [InlineData(@"D:\Apps\thing")]                     // another drive
    [InlineData(@"C:\Program Files\Vendor\App")]       // INSIDE Program Files is fine; Program Files itself isn't
    [InlineData(@"c:/tools/my-app")]                   // forward slashes and lower case
    public void AllowsAProgramsOwnFolder(string path)
        => Assert.Null(InstallPathGuard.WhyUnsafeToDelete(path, AgentRoot, NoneInUse));

    [Theory]
    [InlineData("")]                                   // empty
    [InlineData("   ")]                                // blank
    [InlineData(@"C:\")]                               // a drive root
    [InlineData(@"C:")]                                // ...written without the slash
    [InlineData(@"C:\Windows")]                        // system folders...
    [InlineData(@"c:\windows\system32")]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\Program Files (x86)\")]
    [InlineData(@"C:\ProgramData")]
    [InlineData(@"C:\Users")]                          // all profiles
    [InlineData(@"C:\Users\bob")]                      // one profile
    [InlineData(@"C:\Users\bob\Desktop")]              // a profile's top-level folder
    [InlineData(@"C:\Windows\Orch")]                   // the agent's own folder
    [InlineData(@"C:\Windows\Orch\programs")]          // the folder all programs share
    [InlineData(@"programs\my-app")]                   // relative
    [InlineData(@"\\server\share\app")]                // network share
    [InlineData(@"C:\Tools\..\Windows")]               // escapes via ..
    public void RefusesDangerousFolders(string path)
        => Assert.NotNull(InstallPathGuard.WhyUnsafeToDelete(path, AgentRoot, NoneInUse));

    [Fact]
    public void RefusesAFolderAnotherActiveProgramStillUses()
    {
        var reason = InstallPathGuard.WhyUnsafeToDelete(@"C:\Tools\shared", AgentRoot, new[] { @"C:\Tools\Shared\" });
        Assert.NotNull(reason);   // same folder, different spelling
    }

    [Fact]
    public void RefusesAParentOfAnotherActiveProgramsFolder()
    {
        var reason = InstallPathGuard.WhyUnsafeToDelete(@"C:\Tools", AgentRoot, new[] { @"C:\Tools\other-app" });
        Assert.NotNull(reason);   // deleting C:\Tools would take other-app with it
    }

    [Fact]
    public void AllowsAFolderNextToAnotherActiveProgram()
    {
        var reason = InstallPathGuard.WhyUnsafeToDelete(@"C:\Tools\app-a", AgentRoot, new[] { @"C:\Tools\app-ab", null });
        Assert.Null(reason);   // a sibling whose name merely starts the same is unrelated
    }
}
