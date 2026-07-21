// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Handles the "run this at startup" registrations that go into the Windows registry
//   (the HKLM ...\Run key). It can add an entry so a program launches when the user
//   logs in, or remove that entry later. This is the non-elevated startup path;
//   programs that need admin rights use the Scheduled Task path instead.
// =====================================================================================

using System.Runtime.Versioning;      // for the [SupportedOSPlatform] Windows-only marker
using Microsoft.Extensions.Logging;   // for logging
using Microsoft.Win32;                // for reading/writing the Windows registry
using Orchestrator.Service.Models;    // for ProgramEntry / config

namespace Orchestrator.Service.Services;   // groups this with the other services

public interface IRegistryService   // the contract for registry startup handling
{
    /// <summary>Register (or overwrite) a program's HKLM Run entry for startup.</summary>
    void RegisterStartup(ProgramEntry program);

    /// <summary>Remove a program's HKLM Run entry if present.</summary>
    void RemoveStartup(ProgramEntry program);
}

/// <summary>
/// Manages HKLM\...\CurrentVersion\Run entries. Windows-only; the service targets
/// net8.0-windows and runs as SYSTEM so HKLM writes are permitted.
/// </summary>
[SupportedOSPlatform("windows")]   // tells the compiler this class only runs on Windows
public sealed class RegistryService : IRegistryService
{
    private readonly OrchestratorConfig _config;        // our settings (the Run key path + name prefix)
    private readonly ILogger<RegistryService> _log;     // logger

    public RegistryService(IConfigService configService, ILogger<RegistryService> log)  // dependencies from DI
    {
        _config = configService.Config;   // grab the settings
        _log = log;                       // store the logger
    }

    private string EntryName(ProgramEntry p) => _config.RegistryEntryPrefix + p.Name;  // the registry value name, e.g. "Orch_my-app"

    public void RegisterStartup(ProgramEntry program)
    {
        var command = LaunchCommandBuilder.BuildRunKeyValue(program);   // build the command line to store
        using var key = Registry.LocalMachine.CreateSubKey(_config.StartupRegistryKey, writable: true)   // open (or create) the Run key
            ?? throw new InvalidOperationException($"Cannot open registry key {_config.StartupRegistryKey}");  // fail if we can't
        key.SetValue(EntryName(program), command, RegistryValueKind.String);   // write our entry: name -> command
        _log.LogInformation("Registered startup entry {Name} -> {Command}", EntryName(program), command);  // log it
    }

    public void RemoveStartup(ProgramEntry program)
    {
        using var key = Registry.LocalMachine.OpenSubKey(_config.StartupRegistryKey, writable: true);   // open the Run key for writing
        if (key is null) return;                     // key doesn't exist -> nothing to remove
        var name = EntryName(program);               // the value name we would have created
        if (key.GetValue(name) is not null)          // does our entry actually exist?
        {
            key.DeleteValue(name, throwOnMissingValue: false);   // delete it (don't throw if already gone)
            _log.LogInformation("Removed startup entry {Name}", name);  // log it
        }
    }
}
