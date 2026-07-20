using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using Microsoft.Extensions.Logging;
using Orchestrator.Service.Models;

namespace Orchestrator.Service.Services;

public interface IScheduledTaskService
{
    /// <summary>Create (or replace) an elevated boot-time Scheduled Task for a program.</summary>
    void CreateStartupTask(ProgramEntry program);

    /// <summary>Delete a program's Scheduled Task if it exists. Tolerates a missing task.</summary>
    void RemoveStartupTask(ProgramEntry program);
}

/// <summary>
/// Registers programs marked <c>runAsAdmin</c> as Windows Scheduled Tasks running as
/// SYSTEM (S-1-5-18) with the highest available privilege, triggered at boot. This is
/// the elevation path an HKLM\Run entry cannot provide — Run entries execute in the
/// interactive user's non-elevated context. Uses <c>schtasks.exe /XML</c> so the full
/// command line and principal are expressed declaratively rather than through fragile
/// command-line quoting.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScheduledTaskService : IScheduledTaskService
{
    private const string SystemAccountSid = "S-1-5-18"; // NT AUTHORITY\SYSTEM

    private readonly OrchestratorConfig _config;
    private readonly ILogger<ScheduledTaskService> _log;

    public ScheduledTaskService(IConfigService configService, ILogger<ScheduledTaskService> log)
    {
        _config = configService.Config;
        _log = log;
    }

    public string TaskName(ProgramEntry program) => _config.RegistryEntryPrefix + program.Name;

    public void CreateStartupTask(ProgramEntry program)
    {
        var xml = BuildTaskXml(program);
        // schtasks requires the XML file to be UTF-16.
        var tmp = Path.Combine(Path.GetTempPath(), $"orch-task-{Guid.NewGuid():N}.xml");
        File.WriteAllText(tmp, xml, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
        try
        {
            var (code, output) = RunSchtasks("/Create", "/TN", TaskName(program), "/XML", tmp, "/F");
            if (code != 0)
                throw new InvalidOperationException(
                    $"schtasks /Create failed (exit {code}) for '{TaskName(program)}': {output}");
            _log.LogInformation("Created scheduled task {Task} (SYSTEM, highest privilege, at boot)", TaskName(program));
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    public void RemoveStartupTask(ProgramEntry program)
    {
        var (code, output) = RunSchtasks("/Delete", "/TN", TaskName(program), "/F");
        // Exit code is non-zero when the task does not exist; that is a no-op, not an error.
        if (code == 0)
            _log.LogInformation("Removed scheduled task {Task}", TaskName(program));
        else if (!output.Contains("cannot find", StringComparison.OrdinalIgnoreCase) &&
                 !output.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
            _log.LogWarning("schtasks /Delete for {Task} returned {Code}: {Output}", TaskName(program), code, output);
    }

    private (int ExitCode, string Output) RunSchtasks(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start schtasks.exe");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, (stdout + stderr).Trim());
    }

    /// <summary>
    /// Build a Task Scheduler 1.2 XML document that runs the program as SYSTEM with
    /// the highest available privilege, at boot. Pure and testable.
    /// </summary>
    public static string BuildTaskXml(ProgramEntry program)
    {
        var cmd = LaunchCommandBuilder.Build(program);
        var description = Esc(program.Description ?? $"Orchestrator startup task for {program.Name}");
        var command = Esc(cmd.Executable);
        var arguments = Esc(cmd.Arguments);
        var workingDir = Esc(program.InstallPath);

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-16\"?>");
        sb.AppendLine("<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">");
        sb.AppendLine("  <RegistrationInfo>");
        sb.AppendLine($"    <Description>{description}</Description>");
        sb.AppendLine("    <Author>GitHubOrchestrator</Author>");
        sb.AppendLine("  </RegistrationInfo>");
        sb.AppendLine("  <Triggers>");
        sb.AppendLine("    <BootTrigger>");
        sb.AppendLine("      <Enabled>true</Enabled>");
        sb.AppendLine("    </BootTrigger>");
        sb.AppendLine("  </Triggers>");
        sb.AppendLine("  <Principals>");
        sb.AppendLine("    <Principal id=\"Author\">");
        sb.AppendLine($"      <UserId>{SystemAccountSid}</UserId>");
        sb.AppendLine("      <RunLevel>HighestAvailable</RunLevel>");
        sb.AppendLine("    </Principal>");
        sb.AppendLine("  </Principals>");
        sb.AppendLine("  <Settings>");
        sb.AppendLine("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>");
        sb.AppendLine("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>");
        sb.AppendLine("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>");
        sb.AppendLine("    <AllowHardTerminate>true</AllowHardTerminate>");
        sb.AppendLine("    <StartWhenAvailable>true</StartWhenAvailable>");
        sb.AppendLine("    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>");
        sb.AppendLine("    <IdleSettings>");
        sb.AppendLine("      <StopOnIdleEnd>false</StopOnIdleEnd>");
        sb.AppendLine("      <RestartOnIdle>false</RestartOnIdle>");
        sb.AppendLine("    </IdleSettings>");
        sb.AppendLine("    <AllowStartOnDemand>true</AllowStartOnDemand>");
        sb.AppendLine("    <Enabled>true</Enabled>");
        sb.AppendLine("    <Hidden>false</Hidden>");
        sb.AppendLine("    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>"); // no time limit (long-running agents)
        sb.AppendLine("  </Settings>");
        sb.AppendLine("  <Actions Context=\"Author\">");
        sb.AppendLine("    <Exec>");
        sb.AppendLine($"      <Command>{command}</Command>");
        if (!string.IsNullOrEmpty(arguments))
            sb.AppendLine($"      <Arguments>{arguments}</Arguments>");
        if (!string.IsNullOrEmpty(workingDir))
            sb.AppendLine($"      <WorkingDirectory>{workingDir}</WorkingDirectory>");
        sb.AppendLine("    </Exec>");
        sb.AppendLine("  </Actions>");
        sb.Append("</Task>");
        return sb.ToString();
    }

    private static string Esc(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : SecurityElement.Escape(value) ?? string.Empty;
}
