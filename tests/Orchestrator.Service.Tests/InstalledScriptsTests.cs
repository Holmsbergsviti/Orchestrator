// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Checks that install.ps1 and update-agent.ps1 really are embedded in the agent. Self-
//   update only downloads the exe; these embedded copies are how a machine's on-disk
//   scripts learn about new settings (see InstalledScripts). If one went missing, every
//   update would quietly keep running the script from the machine's first install.
// =====================================================================================

using Orchestrator.Service.Services;   // InstalledScripts
using Xunit;                           // the test framework

namespace Orchestrator.Service.Tests;

public sealed class InstalledScriptsTests
{
    [Theory]
    [InlineData("scripts/install.ps1", "RelayCertThumbprint")]
    [InlineData("scripts/update-agent.ps1", "install.ps1")]
    public void InstallerScripts_AreEmbeddedInTheAgent(string resource, string mustContain)
    {
        using var stream = typeof(InstalledScripts).Assembly.GetManifestResourceStream(resource);
        Assert.NotNull(stream);
        Assert.Contains(mustContain, new StreamReader(stream!).ReadToEnd());
    }
}
