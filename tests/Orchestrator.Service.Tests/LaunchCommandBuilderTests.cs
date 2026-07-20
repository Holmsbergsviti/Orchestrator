using Orchestrator.Service.Models;
using Orchestrator.Service.Services;
using Xunit;

namespace Orchestrator.Service.Tests;

public sealed class LaunchCommandBuilderTests
{
    [Fact]
    public void PowerShell_UsesPowerShellHost_WithBypassAndFile()
    {
        var p = TestData.Program("a", type: ProgramType.PowerShell, installPath: "scripts", fileName: "monitor.ps1", arguments: "-Custom");
        var cmd = LaunchCommandBuilder.Build(p);

        Assert.EndsWith("powershell.exe", cmd.Executable);
        Assert.Contains("-ExecutionPolicy Bypass", cmd.Arguments);
        Assert.Contains("-NoProfile", cmd.Arguments);
        Assert.Contains("-File", cmd.Arguments);
        Assert.Contains("monitor.ps1", cmd.Arguments);
        Assert.Contains("-Custom", cmd.Arguments);
    }

    [Fact]
    public void Batch_UsesCmd()
    {
        var p = TestData.Program("a", type: ProgramType.Batch, fileName: "sync.bat", arguments: "full");
        var cmd = LaunchCommandBuilder.Build(p);

        Assert.Equal("cmd.exe", cmd.Executable);
        Assert.StartsWith("/c ", cmd.Arguments);
        Assert.Contains("sync.bat", cmd.Arguments);
        Assert.Contains("full", cmd.Arguments);
    }

    [Fact]
    public void Vbs_UsesWscript()
    {
        var cmd = LaunchCommandBuilder.Build(TestData.Program("a", type: ProgramType.Vbs, fileName: "x.vbs"));
        Assert.Equal("wscript.exe", cmd.Executable);
    }

    [Fact]
    public void Python_UsesPythonw()
    {
        var cmd = LaunchCommandBuilder.Build(TestData.Program("a", type: ProgramType.Python, fileName: "x.py"));
        Assert.Equal("pythonw", cmd.Executable);
    }

    [Fact]
    public void Exe_WithNoArguments_HasEmptyArguments()
    {
        var cmd = LaunchCommandBuilder.Build(TestData.Program("a", type: ProgramType.Exe, fileName: "app.exe"));
        Assert.Equal(string.Empty, cmd.Arguments);
    }

    [Fact]
    public void RunKeyValue_QuotesPathAndAppendsArguments()
    {
        var p = TestData.Program("a", type: ProgramType.Exe, installPath: @"C:\Program Files\app", fileName: "app.exe", arguments: "--min");
        var value = LaunchCommandBuilder.BuildRunKeyValue(p);

        Assert.StartsWith("\"", value);           // quoted because the path contains a space
        Assert.Contains("app.exe\"", value);
        Assert.EndsWith("--min", value);
    }
}
