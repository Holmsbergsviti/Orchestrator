using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orchestrator.Service.Models;

namespace Orchestrator.Service.Services;

public interface IConfigService
{
    OrchestratorConfig Config { get; }

    /// <summary>Load machine config, generating a MachineID + directories on first run.</summary>
    MachineConfig LoadOrCreateMachineConfig();

    void SaveMachineConfig(MachineConfig config);

    /// <summary>Ensure root/programs/logs/cache directories exist.</summary>
    void EnsureDirectories();
}

public sealed class ConfigService : IConfigService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly ILogger<ConfigService> _log;

    public OrchestratorConfig Config { get; }

    public ConfigService(IOptions<OrchestratorConfig> options, ILogger<ConfigService> log)
    {
        Config = options.Value;
        _log = log;
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Config.RootPath);
        Directory.CreateDirectory(Config.ProgramsPath);
        Directory.CreateDirectory(Config.LogsPath);
        Directory.CreateDirectory(Config.CachePath);
    }

    public MachineConfig LoadOrCreateMachineConfig()
    {
        EnsureDirectories();

        if (File.Exists(Config.MachineConfigPath))
        {
            try
            {
                var json = File.ReadAllText(Config.MachineConfigPath);
                var cfg = JsonSerializer.Deserialize<MachineConfig>(json);
                if (cfg is not null && !string.IsNullOrWhiteSpace(cfg.MachineId))
                    return cfg;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "config.json unreadable; regenerating");
            }
        }

        var created = new MachineConfig
        {
            MachineId = Guid.NewGuid().ToString(),
            FirstRun = DateTimeOffset.UtcNow.ToString("O"),
            Hostname = Environment.MachineName
        };
        SaveMachineConfig(created);
        _log.LogInformation("Generated new MachineID {MachineId}", created.MachineId);
        return created;
    }

    public void SaveMachineConfig(MachineConfig config)
    {
        var json = JsonSerializer.Serialize(config, JsonOpts);
        File.WriteAllText(Config.MachineConfigPath, json);
    }
}
