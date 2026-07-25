// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Stops a program that is currently running when it gets deleted. Removing the files
//   and the startup entry doesn't stop a process that's already going (think of a script
//   stuck in a loop), so on delete we also find and kill it. It matches any process whose
//   command line or executable path points at the program's installed file — that catches
//   both plain .exe programs and scripts running under cmd.exe / powershell.exe.
// =====================================================================================

using System.Diagnostics;             // Process
using System.Management;              // WMI (Win32_Process) to read command lines
using System.Runtime.Versioning;      // [SupportedOSPlatform]
using Microsoft.Extensions.Logging;   // logging

namespace Orchestrator.Service.Services;

[SupportedOSPlatform("windows")]   // WMI + process image paths are Windows-only here
public static class ProcessTerminator
{
    /// <summary>Kill any running process whose command line / image path references <paramref name="fullFilePath"/>. Returns how many were killed.</summary>
    public static int KillByFilePath(string fullFilePath, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(fullFilePath)) return 0;
        var killed = 0;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine, ExecutablePath FROM Win32_Process");
            foreach (var o in searcher.Get())
            {
                using var mo = o;
                var cmdLine = mo["CommandLine"] as string ?? string.Empty;
                var exePath = mo["ExecutablePath"] as string ?? string.Empty;
                var hit = cmdLine.Contains(fullFilePath, StringComparison.OrdinalIgnoreCase)
                          || exePath.Equals(fullFilePath, StringComparison.OrdinalIgnoreCase);
                if (!hit) continue;

                var pid = Convert.ToInt32(mo["ProcessId"]);
                if (pid <= 4) continue;   // never touch System/Idle
                try
                {
                    using var proc = Process.GetProcessById(pid);
                    proc.Kill(entireProcessTree: true);
                    killed++;
                }
                catch (Exception ex)
                {
                    log.LogDebug(ex, "Could not kill process {Pid} for {Path}", pid, fullFilePath);
                }
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Process lookup (kill-on-delete) failed for {Path}", fullFilePath);
        }
        return killed;
    }
}
