// =====================================================================================
// FILE PURPOSE (in plain terms):
//   A traffic cop for startup registration. There are two ways to make a program
//   launch at startup: a normal registry Run entry (non-elevated) or a Scheduled Task
//   running as SYSTEM (elevated). This class picks the right one based on the
//   program's runAsAdmin flag, and — importantly — always clears the OTHER mechanism,
//   so flipping the flag between syncs never leaves a leftover, duplicate launcher.
// =====================================================================================

using System.Runtime.Versioning;    // for the [SupportedOSPlatform] Windows-only marker
using Orchestrator.Service.Models;  // for ProgramEntry

namespace Orchestrator.Service.Services;   // groups this with the other services

public interface IStartupManager   // the contract for startup registration routing
{
    /// <summary>
    /// Ensure the program is registered for startup using the correct mechanism for its
    /// privilege requirement, and clear the other mechanism so a flipped flag never leaves
    /// a stale duplicate launcher behind.
    /// </summary>
    void Register(ProgramEntry program);

    /// <summary>Remove any startup registration for the program (both mechanisms).</summary>
    void Remove(ProgramEntry program);
}

/// <summary>
/// Routes startup registration to the mechanism that matches a program's privilege needs:
/// <list type="bullet">
/// <item><c>runAsAdmin: true</c> → Scheduled Task running as SYSTEM with highest privilege.</item>
/// <item><c>runAsAdmin: false</c> → HKLM\Run entry (interactive user's context).</item>
/// </list>
/// Registering one mechanism always removes the other, so toggling <c>runAsAdmin</c>
/// between syncs migrates the program cleanly instead of double-launching it.
/// </summary>
[SupportedOSPlatform("windows")]   // Windows-only (registry + scheduled tasks)
public sealed class StartupManager : IStartupManager
{
    private readonly IRegistryService _registry;      // the registry Run-entry handler
    private readonly IScheduledTaskService _tasks;    // the scheduled-task handler

    public StartupManager(IRegistryService registry, IScheduledTaskService tasks)  // dependencies from DI
    {
        _registry = registry;   // store the registry handler
        _tasks = tasks;         // store the task handler
    }

    public void Register(ProgramEntry program)
    {
        if (program.RunAsAdmin)   // needs elevation?
        {
            _tasks.CreateStartupTask(program);   // -> use a SYSTEM Scheduled Task
            _registry.RemoveStartup(program);    // and clear any old registry entry
        }
        else
        {
            _registry.RegisterStartup(program);  // -> use a normal registry Run entry
            _tasks.RemoveStartupTask(program);   // and clear any old scheduled task
        }
    }

    public void Remove(ProgramEntry program)
    {
        _registry.RemoveStartup(program);    // remove the registry entry (if any)
        _tasks.RemoveStartupTask(program);   // and the scheduled task (if any)
    }
}
