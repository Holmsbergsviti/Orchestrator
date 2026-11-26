// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Automated checks for the Scheduled Task XML builder. They confirm the XML says
//   "run as SYSTEM, highest privilege, at boot", omits an <Arguments> tag when there
//   are none, includes it for a batch file, properly escapes special characters like
//   & and <, and that the whole thing is valid, well-formed XML.
// =====================================================================================

using Orchestrator.Service.Models;     // for ProgramType
using Orchestrator.Service.Services;   // the code being tested
using Xunit;                           // the test framework

namespace Orchestrator.Service.Tests;   // groups this with the other tests

public sealed class ScheduledTaskServiceTests
{
    [Fact]
    public void BuildTaskXml_RunsAsSystem_WithHighestPrivilege_AtBoot()
    {
        var xml = ScheduledTaskService.BuildTaskXml(   // build the task XML for an elevated program
            TestData.Program("a", type: ProgramType.Exe, installPath: "root", fileName: "agent.exe", runAsAdmin: true),
            @"C:\orch.exe", "run-program a");

        Assert.Contains("<UserId>S-1-5-18</UserId>", xml);          // NT AUTHORITY\SYSTEM
        Assert.Contains("<RunLevel>HighestAvailable</RunLevel>", xml);  // highest privilege
        Assert.Contains("<BootTrigger>", xml);                      // triggered at boot
        Assert.Contains("<Command>", xml);                          // and it runs a command
    }

    [Fact]
    public void BuildTaskXml_UsesGatedLauncherCommandAndArgs()
    {
        var xml = ScheduledTaskService.BuildTaskXml(                    // the task should run the orchestrator launcher
            TestData.Program("a", type: ProgramType.Batch, fileName: "sync.bat"),
            @"C:\orch.exe", "run-program a");

        Assert.Contains(@"<Command>C:\orch.exe</Command>", xml);       // the launcher exe, not cmd.exe
        Assert.Contains("<Arguments>run-program a</Arguments>", xml);  // and the run-program verb
    }

    [Fact]
    public void BuildTaskXml_OmitsArguments_WhenNone()
    {
        var xml = ScheduledTaskService.BuildTaskXml(                    // no exec arguments
            TestData.Program("a", type: ProgramType.Exe, fileName: "agent.exe"), @"C:\orch.exe", "");

        Assert.DoesNotContain("<Arguments>", xml);                     // so there should be no <Arguments> tag
    }

    [Fact]
    public void BuildTaskXml_EscapesSpecialCharactersInDescription()
    {
        var xml = ScheduledTaskService.BuildTaskXml(                    // a description with XML-special characters
            TestData.Program("a", fileName: "agent.exe", description: "A & B <tool>"), @"C:\orch.exe", "run-program a");

        Assert.Contains("A &amp; B &lt;tool&gt;", xml);                // they must be escaped
        Assert.DoesNotContain("A & B <tool>", xml);                    // and not left raw
    }

    [Fact]
    public void BuildTaskXml_IsWellFormedXml()
    {
        var xml = ScheduledTaskService.BuildTaskXml(                    // build XML with tricky quoted arguments
            TestData.Program("a", type: ProgramType.PowerShell, installPath: "s", fileName: "m.ps1"),
            @"C:\orch.exe", "run-program a \"q\"");

        // Drop the <?xml ... encoding="UTF-16"?> prolog: the string is already UTF-16
        // in memory, and we only care that the element tree is well-formed and escaped.
        var body = xml[(xml.IndexOf('\n') + 1)..];                     // everything after the first line (the prolog)
        var ex = Record.Exception(() => System.Xml.Linq.XDocument.Parse(body));  // try to parse it as XML
        Assert.Null(ex);                                               // no exception means it's well-formed
    }

    [Fact]
    public void BuildInteractiveRunOnceXml_RunsAsInteractiveUser_OnceThenSelfDeletes()
    {
        var xml = ScheduledTaskService.BuildInteractiveRunOnceXml(
            TestData.Program("a", type: ProgramType.Batch, installPath: "s", fileName: "x.bat"),
            "cmd.exe", "/c \"s\\x.bat\"", @"PC\Alice", new DateTime(2026, 7, 25, 10, 0, 0));

        Assert.Contains(@"<UserId>PC\Alice</UserId>", xml);                   // the logged-on user
        Assert.Contains("<LogonType>InteractiveToken</LogonType>", xml);     // their interactive session, no password
        Assert.Contains("<TimeTrigger>", xml);                               // fires once, soon
        Assert.Contains("<DeleteExpiredTaskAfter>PT1M</DeleteExpiredTaskAfter>", xml);  // self-cleans
        Assert.Contains("<Command>cmd.exe</Command>", xml);                  // runs the program
    }

    [Fact]
    public void BuildInteractiveRunOnceXml_IsWellFormedXml()
    {
        var xml = ScheduledTaskService.BuildInteractiveRunOnceXml(
            TestData.Program("a", fileName: "app.exe"), @"C:\app.exe", "--x \"q\"", @"PC\Bob", DateTime.Now);
        var body = xml[(xml.IndexOf('\n') + 1)..];
        Assert.Null(Record.Exception(() => System.Xml.Linq.XDocument.Parse(body)));
    }
}
