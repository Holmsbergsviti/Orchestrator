using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Orchestrator.Service.Models;

namespace Orchestrator.Service.Services;

public interface IRegistryService
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
[SupportedOSPlatform("windows")]
public sealed class RegistryService : IRegistryService
{
    private readonly OrchestratorConfig _config;
    private readonly ILogger<RegistryService> _log;

    public RegistryService(IConfigService configService, ILogger<RegistryService> log)
    {
        _config = configService.Config;
        _log = log;
    }

    private string EntryName(ProgramEntry p) => _config.RegistryEntryPrefix + p.Name;

    public void RegisterStartup(ProgramEntry program)
    {
        var command = LaunchCommandBuilder.BuildRunKeyValue(program);
        using var key = Registry.LocalMachine.CreateSubKey(_config.StartupRegistryKey, writable: true)
            ?? throw new InvalidOperationException($"Cannot open registry key {_config.StartupRegistryKey}");
        key.SetValue(EntryName(program), command, RegistryValueKind.String);
        _log.LogInformation("Registered startup entry {Name} -> {Command}", EntryName(program), command);
    }

    public void RemoveStartup(ProgramEntry program)
    {
        using var key = Registry.LocalMachine.OpenSubKey(_config.StartupRegistryKey, writable: true);
        if (key is null) return;
        var name = EntryName(program);
        if (key.GetValue(name) is not null)
        {
            key.DeleteValue(name, throwOnMissingValue: false);
            _log.LogInformation("Removed startup entry {Name}", name);
        }
    }
}
