using Orchestrator.Service.Models;
using Orchestrator.Service.Services;
using Xunit;

namespace Orchestrator.Service.Tests;

public sealed class ScheduledTaskServiceTests
{
    [Fact]
    public void BuildTaskXml_RunsAsSystem_WithHighestPrivilege_AtBoot()
    {
        var xml = ScheduledTaskService.BuildTaskXml(
            TestData.Program("a", type: ProgramType.Exe, installPath: "root", fileName: "agent.exe", runAsAdmin: true));

        Assert.Contains("<UserId>S-1-5-18</UserId>", xml);          // NT AUTHORITY\SYSTEM
        Assert.Contains("<RunLevel>HighestAvailable</RunLevel>", xml);
        Assert.Contains("<BootTrigger>", xml);
        Assert.Contains("<Command>", xml);
    }

    [Fact]
    public void BuildTaskXml_OmitsArguments_ForExeWithNoArgs()
    {
        var xml = ScheduledTaskService.BuildTaskXml(
            TestData.Program("a", type: ProgramType.Exe, fileName: "agent.exe"));

        Assert.DoesNotContain("<Arguments>", xml);
    }

    [Fact]
    public void BuildTaskXml_IncludesArguments_ForBatch()
    {
        var xml = ScheduledTaskService.BuildTaskXml(
            TestData.Program("a", type: ProgramType.Batch, fileName: "sync.bat", arguments: "full"));

        Assert.Contains("<Arguments>", xml);
        Assert.Contains("<Command>cmd.exe</Command>", xml);
    }

    [Fact]
    public void BuildTaskXml_EscapesSpecialCharactersInDescription()
    {
        var xml = ScheduledTaskService.BuildTaskXml(
            TestData.Program("a", fileName: "agent.exe", description: "A & B <tool>"));

        Assert.Contains("A &amp; B &lt;tool&gt;", xml);
        Assert.DoesNotContain("A & B <tool>", xml);
    }

    [Fact]
    public void BuildTaskXml_IsWellFormedXml()
    {
        var xml = ScheduledTaskService.BuildTaskXml(
            TestData.Program("a", type: ProgramType.PowerShell, installPath: "s", fileName: "m.ps1", arguments: "-X \"q\""));

        // Drop the <?xml ... encoding="UTF-16"?> prolog: the string is already UTF-16
        // in memory, and we only care that the element tree is well-formed and escaped.
        var body = xml[(xml.IndexOf('\n') + 1)..];
        var ex = Record.Exception(() => System.Xml.Linq.XDocument.Parse(body));
        Assert.Null(ex);
    }
}
