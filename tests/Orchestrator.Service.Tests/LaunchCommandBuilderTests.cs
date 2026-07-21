// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Automated checks for the "how do we launch each program type" logic. They confirm
//   PowerShell scripts run through powershell.exe with the right flags, batch files
//   through cmd.exe, .vbs through wscript, .py through pythonw, a plain exe has no
//   extra arguments, and that a spaced path gets quoted in the registry Run value.
// =====================================================================================

using Orchestrator.Service.Models;     // for ProgramType
using Orchestrator.Service.Services;   // the code being tested
using Xunit;                           // the test framework

namespace Orchestrator.Service.Tests;   // groups this with the other tests

public sealed class LaunchCommandBuilderTests
{
    [Fact]
    public void PowerShell_UsesPowerShellHost_WithBypassAndFile()
    {
        var p = TestData.Program("a", type: ProgramType.PowerShell, installPath: "scripts", fileName: "monitor.ps1", arguments: "-Custom");  // a .ps1 program
        var cmd = LaunchCommandBuilder.Build(p);   // build its launch command

        Assert.EndsWith("powershell.exe", cmd.Executable);        // launched by powershell.exe
        Assert.Contains("-ExecutionPolicy Bypass", cmd.Arguments); // with the bypass flag
        Assert.Contains("-NoProfile", cmd.Arguments);             // and no-profile
        Assert.Contains("-File", cmd.Arguments);                  // pointing at a file
        Assert.Contains("monitor.ps1", cmd.Arguments);            // the right file
        Assert.Contains("-Custom", cmd.Arguments);                // and our extra argument is kept
    }

    [Fact]
    public void Batch_UsesCmd()
    {
        var p = TestData.Program("a", type: ProgramType.Batch, fileName: "sync.bat", arguments: "full");  // a .bat program
        var cmd = LaunchCommandBuilder.Build(p);

        Assert.Equal("cmd.exe", cmd.Executable);       // launched by cmd.exe
        Assert.StartsWith("/c ", cmd.Arguments);       // with the /c switch
        Assert.Contains("sync.bat", cmd.Arguments);    // the right file
        Assert.Contains("full", cmd.Arguments);        // and our extra argument
    }

    [Fact]
    public void Vbs_UsesWscript()
    {
        var cmd = LaunchCommandBuilder.Build(TestData.Program("a", type: ProgramType.Vbs, fileName: "x.vbs"));  // a .vbs program
        Assert.Equal("wscript.exe", cmd.Executable);   // launched by wscript.exe
    }

    [Fact]
    public void Python_UsesPythonw()
    {
        var cmd = LaunchCommandBuilder.Build(TestData.Program("a", type: ProgramType.Python, fileName: "x.py"));  // a .py program
        Assert.Equal("pythonw", cmd.Executable);       // launched by pythonw
    }

    [Fact]
    public void Exe_WithNoArguments_HasEmptyArguments()
    {
        var cmd = LaunchCommandBuilder.Build(TestData.Program("a", type: ProgramType.Exe, fileName: "app.exe"));  // a plain exe, no args
        Assert.Equal(string.Empty, cmd.Arguments);     // its arguments should be empty
    }

    [Fact]
    public void RunKeyValue_QuotesPathAndAppendsArguments()
    {
        var p = TestData.Program("a", type: ProgramType.Exe, installPath: @"C:\Program Files\app", fileName: "app.exe", arguments: "--min");  // path with a space
        var value = LaunchCommandBuilder.BuildRunKeyValue(p);   // build the registry Run value

        Assert.StartsWith("\"", value);           // quoted because the path contains a space
        Assert.Contains("app.exe\"", value);      // the quoted exe path
        Assert.EndsWith("--min", value);          // followed by the argument
    }
}
