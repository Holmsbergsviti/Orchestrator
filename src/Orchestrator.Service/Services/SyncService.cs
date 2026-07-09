using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Orchestrator.Service.Models;

namespace Orchestrator.Service.Services;

public interface ISyncService
{
    /// <summary>Run one full sync cycle. Never throws; failures are logged and recorded.</summary>
    Task<SyncRecord> RunSyncAsync(CancellationToken ct = default);
}

public sealed class SyncService : ISyncService
{
    private const int MaxHistoryRecords = 200;

    private readonly IGitHubClient _github;
    private readonly IManifestService _manifests;
    private readonly IChecksumService _checksums;
    private readonly IRegistryService _registry;
    private readonly IConfigService _configService;
    private readonly ILogger<SyncService> _log;
    private readonly OrchestratorConfig _config;

    public SyncService(
        IGitHubClient github,
        IManifestService manifests,
        IChecksumService checksums,
        IRegistryService registry,
        IConfigService configService,
        ILogger<SyncService> log)
    {
        _github = github;
        _manifests = manifests;
        _checksums = checksums;
        _registry = registry;
        _configService = configService;
        _config = configService.Config;
        _log = log;
    }

    public async Task<SyncRecord> RunSyncAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var record = new SyncRecord();
        _log.LogInformation("========== SYNC CYCLE STARTED ==========");

        try
        {
            _configService.EnsureDirectories();

            _log.LogInformation("Fetching manifest from GitHub...");
            var remote = await _github.GetManifestAsync(ct);
            if (remote is null)
            {
                record.Success = false;
                record.Errors.Add("Manifest fetch failed; keeping current state.");
                _log.LogWarning("Manifest unavailable — skipping cycle, will retry next interval");
                return Finish(record, sw);
            }

            record.ManifestVersion = remote.Version;
            _log.LogInformation("Manifest fetched (v{Version}). {Active} active, {Deleted} deleted",
                remote.Version, remote.ActivePrograms.Count(), remote.DeletedPrograms.Count());

            var local = _manifests.LoadLocalManifest();
            var checksumCache = _manifests.LoadChecksumCache();
            var plan = _manifests.BuildPlan(remote, local, checksumCache);

            if (!plan.HasWork)
                _log.LogInformation("Everything up to date.");

            foreach (var action in plan.Actions)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    switch (action.Type)
                    {
                        case SyncActionType.Install:
                        case SyncActionType.Update:
                            await InstallAsync(action, checksumCache, ct);
                            (action.Type == SyncActionType.Install ? record.Installed : record.Updated)
                                .Add($"{action.Program.Name} v{action.Program.Version}");
                            break;
                        case SyncActionType.Delete:
                            DeleteProgram(action.Program, checksumCache);
                            record.Deleted.Add(action.Program.Name);
                            break;
                        case SyncActionType.UpToDate:
                            break;
                    }
                }
                catch (Exception ex)
                {
                    var msg = $"{action.Type} {action.Program.Name}: {ex.Message}";
                    record.Errors.Add(msg);
                    _log.LogError(ex, "Action failed: {Action}", action);
                }
            }

            // Persist new state so next cycle diffs correctly.
            _manifests.SaveChecksumCache(checksumCache);
            _manifests.SaveLocalManifest(remote);

            record.Success = record.Errors.Count == 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            record.Success = false;
            record.Errors.Add(ex.Message);
            _log.LogError(ex, "Sync cycle failed");
        }

        return Finish(record, sw);
    }

    private SyncRecord Finish(SyncRecord record, Stopwatch sw)
    {
        sw.Stop();
        record.DurationSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2);
        AppendHistory(record);
        _log.LogInformation("Sync {Status} in {Seconds}s (installed {I}, updated {U}, deleted {D}, errors {E})",
            record.Success ? "completed" : "completed with errors",
            record.DurationSeconds, record.Installed.Count, record.Updated.Count,
            record.Deleted.Count, record.Errors.Count);
        _log.LogInformation("========== SYNC CYCLE COMPLETED ==========");
        return record;
    }

    private async Task InstallAsync(SyncAction action, Dictionary<string, string> checksumCache, CancellationToken ct)
    {
        var p = action.Program;
        var verb = action.Type == SyncActionType.Update
            ? $"Updating {p.Name} v{action.PreviousVersion} -> v{p.Version}"
            : $"Installing {p.Name} v{p.Version}";
        _log.LogInformation("{Verb}", verb);

        var bytes = await _github.DownloadFileAsync(p, ct);
        _log.LogInformation("Downloaded {Kb:N1} KB", bytes.Length / 1024.0);

        if (!_checksums.Verify(bytes, p.NormalizedChecksum))
        {
            var actual = _checksums.ComputeSha256(bytes);
            throw new InvalidOperationException(
                $"Checksum mismatch. expected={p.NormalizedChecksum} actual={actual}");
        }

        if (p.NormalizedChecksum is not null)
            _log.LogInformation("Checksum verified");
        else
            _log.LogWarning("No checksum in manifest for {Name} — installing unverified", p.Name);

        Directory.CreateDirectory(p.InstallPath);
        var target = p.FullFilePath;
        // Write to temp then move to make replacement atomic-ish and avoid partial files.
        var tmp = target + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes, ct);
        File.Move(tmp, target, overwrite: true);
        _log.LogInformation("Installed to {Path}", p.InstallPath);

        checksumCache[p.Id] = _checksums.ComputeSha256(bytes);

        if (p.RunAtStartup && OperatingSystem.IsWindows())
            _registry.RegisterStartup(p);

        if (p.RunOnce)
            MaybeRunOnce(p);
    }

    private void DeleteProgram(ProgramEntry p, Dictionary<string, string> checksumCache)
    {
        _log.LogInformation("Deleting {Name}{Reason}", p.Name,
            string.IsNullOrWhiteSpace(p.Reason) ? "" : $" ({p.Reason})");

        if (OperatingSystem.IsWindows())
            _registry.RemoveStartup(p);

        if (!string.IsNullOrWhiteSpace(p.InstallPath) && Directory.Exists(p.InstallPath))
        {
            try { Directory.Delete(p.InstallPath, recursive: true); }
            catch (Exception ex) { _log.LogWarning(ex, "Could not delete {Path}", p.InstallPath); }
            _log.LogInformation("Removed {Path}", p.InstallPath);
        }

        checksumCache.Remove(p.Id);
    }

    [SupportedOSPlatform("windows")]
    private void MaybeRunOnce(ProgramEntry p)
    {
        if (!OperatingSystem.IsWindows()) return;

        var machine = _configService.LoadOrCreateMachineConfig();
        if (machine.CompletedRunOnce.Contains(p.Id)) return;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = p.FullFilePath,
                Arguments = p.Arguments ?? string.Empty,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = p.InstallPath
            };
            Process.Start(psi);
            machine.CompletedRunOnce.Add(p.Id);
            _configService.SaveMachineConfig(machine);
            _log.LogInformation("Executed runOnce program {Name}", p.Name);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "runOnce launch failed for {Name}", p.Name);
        }
    }

    private void AppendHistory(SyncRecord record)
    {
        try
        {
            SyncHistory history = new();
            if (File.Exists(_config.SyncHistoryPath))
            {
                history = JsonSerializer.Deserialize<SyncHistory>(
                    File.ReadAllText(_config.SyncHistoryPath)) ?? new();
            }
            history.Records.Add(record);
            if (history.Records.Count > MaxHistoryRecords)
                history.Records.RemoveRange(0, history.Records.Count - MaxHistoryRecords);

            File.WriteAllText(_config.SyncHistoryPath,
                JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not write sync-history.json");
        }
    }
}
