// =====================================================================================
// FILE PURPOSE (in plain terms):
//   The elevated startup path. For programs that need admin rights, this registers a
//   Windows Scheduled Task that runs the program as SYSTEM (full privilege) at boot —
//   something a plain registry Run entry can't do. It builds a Task Scheduler XML
//   description of the task and hands it to the built-in schtasks.exe tool to create
//   or delete. The XML builder is pure text and so can be unit-tested anywhere.
// =====================================================================================

using System.Diagnostics;             // for running schtasks.exe as a child process
using System.Runtime.Versioning;      // for the [SupportedOSPlatform] Windows-only marker
using System.Security;                // for SecurityElement.Escape (XML escaping)
using System.Text;                    // for StringBuilder and UnicodeEncoding
using Microsoft.Extensions.Logging;   // for logging
using Orchestrator.Service.Models;    // for ProgramEntry / config

namespace Orchestrator.Service.Services;   // groups this with the other services

public interface IScheduledTaskService   // the contract for scheduled-task handling
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
[SupportedOSPlatform("windows")]   // Windows-only
public sealed class ScheduledTaskService : IScheduledTaskService
{
    private const string SystemAccountSid = "S-1-5-18"; // NT AUTHORITY\SYSTEM

    private readonly OrchestratorConfig _config;             // our settings (the name prefix)
    private readonly ILogger<ScheduledTaskService> _log;     // logger

    public ScheduledTaskService(IConfigService configService, ILogger<ScheduledTaskService> log)  // dependencies from DI
    {
        _config = configService.Config;   // grab the settings
        _log = log;                       // store the logger
    }

    public string TaskName(ProgramEntry program) => _config.RegistryEntryPrefix + program.Name;  // task name, e.g. "Orch_my-app"

    public void CreateStartupTask(ProgramEntry program)
    {
        var xml = BuildTaskXml(program);   // build the task definition as XML text
        // schtasks requires the XML file to be UTF-16.
        var tmp = Path.Combine(Path.GetTempPath(), $"orch-task-{Guid.NewGuid():N}.xml");   // a unique temp file for the XML
        File.WriteAllText(tmp, xml, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));  // write it as UTF-16 with a BOM
        try
        {
            var (code, output) = RunSchtasks("/Create", "/TN", TaskName(program), "/XML", tmp, "/F");  // create the task from the XML (/F = overwrite)
            if (code != 0)   // non-zero exit means it failed
                throw new InvalidOperationException(
                    $"schtasks /Create failed (exit {code}) for '{TaskName(program)}': {output}");
            _log.LogInformation("Created scheduled task {Task} (SYSTEM, highest privilege, at boot)", TaskName(program));  // log success
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best effort */ }   // always clean up the temp XML file
        }
    }

    public void RemoveStartupTask(ProgramEntry program)
    {
        var (code, output) = RunSchtasks("/Delete", "/TN", TaskName(program), "/F");   // delete the task (/F = no prompt)
        // Exit code is non-zero when the task does not exist; that is a no-op, not an error.
        if (code == 0)
            _log.LogInformation("Removed scheduled task {Task}", TaskName(program));   // success -> log it
        else if (!output.Contains("cannot find", StringComparison.OrdinalIgnoreCase) &&   // it failed, but not just because...
                 !output.Contains("does not exist", StringComparison.OrdinalIgnoreCase))  // ...the task was already gone
            _log.LogWarning("schtasks /Delete for {Task} returned {Code}: {Output}", TaskName(program), code, output);  // a real problem -> warn
    }

    private (int ExitCode, string Output) RunSchtasks(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")   // set up how to launch schtasks.exe
        {
            RedirectStandardOutput = true,   // capture its normal output
            RedirectStandardError = true,    // capture its error output
            UseShellExecute = false,         // run it directly (needed to capture output)
            CreateNoWindow = true            // don't pop up a console window
        };
        foreach (var a in args) psi.ArgumentList.Add(a);   // add each argument safely (no manual quoting)

        using var proc = Process.Start(psi)   // start the process
            ?? throw new InvalidOperationException("Could not start schtasks.exe");
        var stdout = proc.StandardOutput.ReadToEnd();   // read all normal output
        var stderr = proc.StandardError.ReadToEnd();    // read all error output
        proc.WaitForExit();                             // wait for it to finish
        return (proc.ExitCode, (stdout + stderr).Trim());  // return the exit code and combined output
    }

    /// <summary>
    /// Build a Task Scheduler 1.2 XML document that runs the program as SYSTEM with
    /// the highest available privilege, at boot. Pure and testable.
    /// </summary>
    public static string BuildTaskXml(ProgramEntry program)
    {
        var cmd = LaunchCommandBuilder.Build(program);   // work out the executable + arguments to run
        var description = Esc(program.Description ?? $"Orchestrator startup task for {program.Name}");  // XML-safe description
        var command = Esc(cmd.Executable);               // XML-safe executable path
        var arguments = Esc(cmd.Arguments);              // XML-safe arguments
        var workingDir = Esc(program.InstallPath);       // XML-safe working directory

        var sb = new StringBuilder();   // build the XML text piece by piece
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-16\"?>");   // XML declaration (must say UTF-16 for schtasks)
        sb.AppendLine("<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">");  // root Task element
        sb.AppendLine("  <RegistrationInfo>");                          // metadata about the task
        sb.AppendLine($"    <Description>{description}</Description>");  // its description
        sb.AppendLine("    <Author>GitHubOrchestrator</Author>");       // who created it
        sb.AppendLine("  </RegistrationInfo>");
        sb.AppendLine("  <Triggers>");            // when the task should run...
        sb.AppendLine("    <BootTrigger>");       // ...at system boot
        sb.AppendLine("      <Enabled>true</Enabled>");
        sb.AppendLine("    </BootTrigger>");
        sb.AppendLine("  </Triggers>");
        sb.AppendLine("  <Principals>");          // who the task runs as...
        sb.AppendLine("    <Principal id=\"Author\">");
        sb.AppendLine($"      <UserId>{SystemAccountSid}</UserId>");    // ...the SYSTEM account
        sb.AppendLine("      <RunLevel>HighestAvailable</RunLevel>");   // ...with highest privilege
        sb.AppendLine("    </Principal>");
        sb.AppendLine("  </Principals>");
        sb.AppendLine("  <Settings>");            // various task behavior settings...
        sb.AppendLine("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>");   // don't start a second copy if one is running
        sb.AppendLine("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>"); // run even on battery
        sb.AppendLine("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>");         // don't stop when switching to battery
        sb.AppendLine("    <AllowHardTerminate>true</AllowHardTerminate>");                  // allow Windows to force-stop it
        sb.AppendLine("    <StartWhenAvailable>true</StartWhenAvailable>");                  // catch up if a start was missed
        sb.AppendLine("    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>");   // don't require a network
        sb.AppendLine("    <IdleSettings>");
        sb.AppendLine("      <StopOnIdleEnd>false</StopOnIdleEnd>");                         // ignore idle-based stopping
        sb.AppendLine("      <RestartOnIdle>false</RestartOnIdle>");
        sb.AppendLine("    </IdleSettings>");
        sb.AppendLine("    <AllowStartOnDemand>true</AllowStartOnDemand>");                  // allow manual start
        sb.AppendLine("    <Enabled>true</Enabled>");                                        // the task is enabled
        sb.AppendLine("    <Hidden>false</Hidden>");                                         // visible in Task Scheduler
        sb.AppendLine("    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>"); // no time limit (long-running agents)
        sb.AppendLine("  </Settings>");
        sb.AppendLine("  <Actions Context=\"Author\">");   // what the task actually does...
        sb.AppendLine("    <Exec>");                        // ...run a program
        sb.AppendLine($"      <Command>{command}</Command>");   // the executable to run
        if (!string.IsNullOrEmpty(arguments))              // only include arguments if there are any
            sb.AppendLine($"      <Arguments>{arguments}</Arguments>");
        if (!string.IsNullOrEmpty(workingDir))             // only include a working dir if we have one
            sb.AppendLine($"      <WorkingDirectory>{workingDir}</WorkingDirectory>");
        sb.AppendLine("    </Exec>");
        sb.AppendLine("  </Actions>");
        sb.Append("</Task>");   // close the root element (no trailing newline)
        return sb.ToString();   // return the finished XML text
    }

    private static string Esc(string? value) =>   // make a string safe to drop into XML
        string.IsNullOrEmpty(value) ? string.Empty : SecurityElement.Escape(value) ?? string.Empty;  // escape &, <, >, etc.
}
