using Orchestrator.Service.Models;
using Orchestrator.Service.Services;

namespace Orchestrator.Service;

/// <summary>
/// Background loop: runs an immediate sync at startup, then every SyncIntervalMinutes.
/// </summary>
public sealed class Worker : BackgroundService
{
    private readonly ISyncService _sync;
    private readonly IConfigService _configService;
    private readonly ILogger<Worker> _log;
    private readonly OrchestratorConfig _config;

    public Worker(ISyncService sync, IConfigService configService, ILogger<Worker> log)
    {
        _sync = sync;
        _configService = configService;
        _config = configService.Config;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var machine = _configService.LoadOrCreateMachineConfig();
        _log.LogInformation("Orchestrator started. MachineID={MachineId} Host={Host} Repo={Owner}/{Repo}@{Branch} Interval={Min}min",
            machine.MachineId, machine.Hostname, _config.RepoOwner, _config.RepoName, _config.Branch, _config.SyncIntervalMinutes);

        var interval = TimeSpan.FromMinutes(Math.Max(1, _config.SyncIntervalMinutes));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _sync.RunSyncAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Should not happen (RunSyncAsync swallows), but never let the loop die.
                _log.LogError(ex, "Unhandled sync error");
            }

            _log.LogInformation("Next sync in {Min} minutes ({Time:u})",
                _config.SyncIntervalMinutes, DateTimeOffset.Now.Add(interval));

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _log.LogInformation("Orchestrator stopping.");
    }
}
