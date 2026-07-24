// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Reports this machine's state back to GitHub after each sync. It builds a Heartbeat,
//   decides whether it's worth committing (did anything meaningful change, or has it
//   been long enough that "last seen" should refresh?), makes sure the fleet-state
//   branch exists, then commits state/<machineId>.json to it. It's best-effort: any
//   failure here is logged but never affects the sync itself.
// =====================================================================================

using System.Reflection;                   // to read the service's own version
using System.Runtime.InteropServices;      // for the OS description string
using System.Text;                         // for UTF-8 encoding the heartbeat JSON
using System.Text.Json;                    // for reading/writing heartbeat JSON
using Microsoft.Extensions.Logging;        // for logging
using Orchestrator.Service.Models;         // for Heartbeat / SyncRecord / config

namespace Orchestrator.Service.Services;   // groups this with the other services

public interface IFleetReporter   // the contract for reporting fleet state
{
    /// <summary>Report this machine's state to GitHub. Never throws; failures are logged.</summary>
    Task ReportAsync(SyncRecord record, CancellationToken ct = default);
}

public sealed class FleetReporter : IFleetReporter
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly string AgentVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";   // the exe's version, once

    private readonly IGitHubClient _github;
    private readonly IManifestService _manifests;
    private readonly IConfigService _configService;
    private readonly ILogger<FleetReporter> _log;
    private readonly OrchestratorConfig _config;

    public FleetReporter(
        IGitHubClient github,
        IManifestService manifests,
        IConfigService configService,
        ILogger<FleetReporter> log)
    {
        _github = github;
        _manifests = manifests;
        _configService = configService;
        _config = configService.Config;
        _log = log;
    }

    public async Task ReportAsync(SyncRecord record, CancellationToken ct = default)
    {
        if (!_config.ReportState) return;   // reporting disabled for this machine

        try
        {
            var machine = _configService.LoadOrCreateMachineConfig();     // who am I
            var applied = (_manifests.LoadLocalManifest()?.ActivePrograms // what this machine currently runs...
                .Select(p => p.Id)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)       // ...in a stable order (so the signature is stable)
                .ToList()) ?? new List<string>();

            var current = new Heartbeat
            {
                MachineId = machine.MachineId,
                Hostname = machine.Hostname,
                Os = RuntimeInformation.OSDescription,
                AgentVersion = AgentVersion,
                LastSeenUtc = DateTimeOffset.UtcNow.ToString("O"),
                SyncIntervalMinutes = _config.SyncIntervalMinutes,
                LastSyncSuccess = record.Success,
                ManifestVersion = record.ManifestVersion,
                AppliedProgramIds = applied,
                LastError = record.Errors.Count > 0 ? record.Errors[0] : null
            };

            var last = LoadLastHeartbeat();   // the last one we committed (if any)
            if (!ShouldPush(current, last, TimeSpan.FromMinutes(Math.Max(1, _config.HeartbeatMaxIntervalMinutes)), DateTimeOffset.UtcNow))
            {
                _log.LogDebug("Heartbeat unchanged — skipping commit");
                return;
            }

            var branch = _config.FleetStateBranch;
            if (!await _github.EnsureBranchAsync(branch, _config.Branch, ct))   // make sure fleet-state exists
            {
                _log.LogWarning("Could not ensure branch '{Branch}'; skipping heartbeat", branch);
                return;
            }

            var path = $"state/{machine.MachineId}.json";
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(current, JsonOpts));
            var sha = await _github.GetFileShaAsync(path, branch, ct);   // null on first push -> create; otherwise update
            var message = $"heartbeat: {machine.Hostname} ({machine.MachineId}) {current.LastSeenUtc}";

            await _github.PutFileAsync(path, bytes, branch, message, sha, ct);
            SaveLastHeartbeat(current);   // remember what we pushed so next cycle can diff
            _log.LogInformation("Reported heartbeat to {Branch}:{Path}", branch, path);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // service is stopping — not an error
        }
        catch (GitHubWriteForbiddenException ex)
        {
            _log.LogWarning("Heartbeat not sent: {Message} Set ReportState=false to silence, or grant the token write access.", ex.Message);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Heartbeat reporting failed (continuing)");   // best-effort — never break the sync
        }
    }

    /// <summary>
    /// Decide whether to commit a heartbeat: push if it's the first one, if anything
    /// meaningful changed (see <see cref="Heartbeat.Signature"/>), or if the last push
    /// is older than <paramref name="maxInterval"/> (so "last seen" stays reasonably fresh).
    /// </summary>
    public static bool ShouldPush(Heartbeat current, Heartbeat? last, TimeSpan maxInterval, DateTimeOffset now)
    {
        if (last is null) return true;                                   // never pushed before
        if (!string.Equals(current.Signature, last.Signature, StringComparison.Ordinal)) return true;  // real change
        if (!DateTimeOffset.TryParse(last.LastSeenUtc, out var lastSeen)) return true;  // unparseable -> refresh
        return now - lastSeen >= maxInterval;                           // otherwise only refresh once it's gone stale
    }

    private Heartbeat? LoadLastHeartbeat()
    {
        try
        {
            if (!File.Exists(_config.LastHeartbeatPath)) return null;
            return JsonSerializer.Deserialize<Heartbeat>(File.ReadAllText(_config.LastHeartbeatPath), JsonOpts);
        }
        catch
        {
            return null;   // unreadable -> treat as "no previous heartbeat"
        }
    }

    private void SaveLastHeartbeat(Heartbeat hb)
    {
        try
        {
            File.WriteAllText(_config.LastHeartbeatPath, JsonSerializer.Serialize(hb, JsonOpts));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not cache last heartbeat");   // non-fatal; worst case we re-push next cycle
        }
    }
}
