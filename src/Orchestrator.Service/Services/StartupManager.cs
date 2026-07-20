using System.Runtime.Versioning;
using Orchestrator.Service.Models;

namespace Orchestrator.Service.Services;

public interface IStartupManager
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
[SupportedOSPlatform("windows")]
public sealed class StartupManager : IStartupManager
{
    private readonly IRegistryService _registry;
    private readonly IScheduledTaskService _tasks;

    public StartupManager(IRegistryService registry, IScheduledTaskService tasks)
    {
        _registry = registry;
        _tasks = tasks;
    }

    public void Register(ProgramEntry program)
    {
        if (program.RunAsAdmin)
        {
            _tasks.CreateStartupTask(program);
            _registry.RemoveStartup(program);
        }
        else
        {
            _registry.RegisterStartup(program);
            _tasks.RemoveStartupTask(program);
        }
    }

    public void Remove(ProgramEntry program)
    {
        _registry.RemoveStartup(program);
        _tasks.RemoveStartupTask(program);
    }
}
