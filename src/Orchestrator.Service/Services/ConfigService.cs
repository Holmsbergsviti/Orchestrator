// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Manages the service's own bookkeeping on disk. It hands out the settings object,
//   makes sure the working folders (programs/logs/cache) exist, and reads or creates
//   this machine's config.json — which holds a unique machine ID generated the first
//   time the service ever runs here.
// =====================================================================================

using System.Text.Json;                    // for reading/writing JSON
using Microsoft.Extensions.Logging;        // for logging
using Microsoft.Extensions.Options;        // for reading the bound config (IOptions<>)
using Orchestrator.Service.Models;         // for the config model classes

namespace Orchestrator.Service.Services;   // groups this with the other services

public interface IConfigService   // the contract for config access
{
    OrchestratorConfig Config { get; }   // the static settings from appsettings.json

    /// <summary>Load machine config, generating a MachineID + directories on first run.</summary>
    MachineConfig LoadOrCreateMachineConfig();

    void SaveMachineConfig(MachineConfig config);   // persist machine state to config.json

    /// <summary>Ensure root/programs/logs/cache directories exist.</summary>
    void EnsureDirectories();
}

public sealed class ConfigService : IConfigService   // the actual implementation
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };  // pretty-print JSON we write

    private readonly ILogger<ConfigService> _log;   // logger

    public OrchestratorConfig Config { get; }       // the settings object (set in the constructor)

    public ConfigService(IOptions<OrchestratorConfig> options, ILogger<ConfigService> log)  // dependencies from DI
    {
        Config = options.Value;   // grab the bound settings
        _log = log;               // store the logger
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Config.RootPath);       // make the base folder if missing
        Directory.CreateDirectory(Config.ProgramsPath);   // make <root>\programs
        Directory.CreateDirectory(Config.LogsPath);       // make <root>\logs
        Directory.CreateDirectory(Config.CachePath);      // make <root>\cache
    }

    public MachineConfig LoadOrCreateMachineConfig()
    {
        EnsureDirectories();   // guarantee the folders exist first

        if (File.Exists(Config.MachineConfigPath))   // do we already have a config.json?
        {
            try
            {
                var json = File.ReadAllText(Config.MachineConfigPath);       // read the file
                var cfg = JsonSerializer.Deserialize<MachineConfig>(json);   // turn it into an object
                if (cfg is not null && !string.IsNullOrWhiteSpace(cfg.MachineId))  // looks valid (has a machine ID)?
                    return cfg;                                              // use it
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "config.json unreadable; regenerating");  // corrupt file -> fall through and recreate
            }
        }

        var created = new MachineConfig            // first run (or bad file): build a fresh config
        {
            MachineId = Guid.NewGuid().ToString(),           // a brand-new unique ID for this machine
            FirstRun = DateTimeOffset.UtcNow.ToString("O"),  // record the current time as first-run
            Hostname = Environment.MachineName               // record this computer's name
        };
        SaveMachineConfig(created);                                          // write it to disk
        _log.LogInformation("Generated new MachineID {MachineId}", created.MachineId);  // note it in the log
        return created;                                                      // and return it
    }

    public void SaveMachineConfig(MachineConfig config)
    {
        var json = JsonSerializer.Serialize(config, JsonOpts);   // convert the object to pretty JSON text
        File.WriteAllText(Config.MachineConfigPath, json);       // save it to config.json
    }
}
