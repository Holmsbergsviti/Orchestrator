// =====================================================================================
// FILE PURPOSE (in plain terms):
//   The heartbeat of the service. Once started, this runs one sync right away, then
//   sleeps for the configured number of minutes and syncs again — forever, until the
//   service is told to stop. It deliberately never lets a single failed sync crash
//   the loop; it logs the problem and waits for the next interval.
// =====================================================================================

using Orchestrator.Service.Models;     // for OrchestratorConfig / machine config
using Orchestrator.Service.Services;   // for ISyncService / IConfigService

namespace Orchestrator.Service;         // top-level service namespace

/// <summary>
/// Background loop: runs an immediate sync at startup, then every SyncIntervalMinutes.
/// </summary>
public sealed class Worker : BackgroundService   // BackgroundService = a long-running task the host manages
{
    private readonly ISyncService _sync;                // does the actual sync work
    private readonly IConfigService _configService;     // loads/saves config
    private readonly ILogger<Worker> _log;              // writes log messages
    private readonly OrchestratorConfig _config;        // our settings (interval, repo, etc.)

    public Worker(ISyncService sync, IConfigService configService, ILogger<Worker> log)  // dependencies handed in by DI
    {
        _sync = sync;                       // store the sync service
        _configService = configService;     // store the config service
        _config = configService.Config;     // grab the settings object for convenience
        _log = log;                         // store the logger
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)  // runs when the service starts; token fires on stop
    {
        var machine = _configService.LoadOrCreateMachineConfig();  // load this machine's ID (or create it on first run)
        _log.LogInformation("Orchestrator started. MachineID={MachineId} Host={Host} Repo={Owner}/{Repo}@{Branch} Interval={Min}min",
            machine.MachineId, machine.Hostname, _config.RepoOwner, _config.RepoName, _config.Branch, _config.SyncIntervalMinutes);  // log the startup details

        var interval = TimeSpan.FromMinutes(Math.Max(1, _config.SyncIntervalMinutes));  // wait time between syncs (at least 1 minute)

        while (!stoppingToken.IsCancellationRequested)  // keep looping until the service is asked to stop
        {
            try
            {
                await _sync.RunSyncAsync(stoppingToken);  // run one full sync cycle
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)  // stop was requested mid-sync
            {
                break;                                   // exit the loop cleanly
            }
            catch (Exception ex)
            {
                // Should not happen (RunSyncAsync swallows), but never let the loop die.
                _log.LogError(ex, "Unhandled sync error");  // log and keep going
            }

            _log.LogInformation("Next sync in {Min} minutes ({Time:u})",
                _config.SyncIntervalMinutes, DateTimeOffset.Now.Add(interval));  // announce when the next sync will be

            try
            {
                await Task.Delay(interval, stoppingToken);  // sleep until the next cycle (wakes early if stopped)
            }
            catch (OperationCanceledException)
            {
                break;                                      // stop requested during the wait -> exit the loop
            }
        }

        _log.LogInformation("Orchestrator stopping.");  // final message as the loop ends
    }
}
