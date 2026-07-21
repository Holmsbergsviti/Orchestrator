// =====================================================================================
// FILE PURPOSE (in plain terms):
//   The "doer" that carries out one full sync. Each cycle it: fetches the manifest,
//   asks ManifestService for a to-do list, then walks that list — downloading and
//   verifying each new/changed file, installing it, registering startup, running any
//   run-once programs, and deleting programs that should go away. It records what
//   happened into sync-history.json and is careful never to crash the whole loop.
// =====================================================================================

using System.Diagnostics;             // for Stopwatch (timing) and launching run-once programs
using System.Runtime.Versioning;      // for the [SupportedOSPlatform] Windows-only marker
using System.Text.Json;               // for reading/writing sync-history.json
using Microsoft.Extensions.Logging;   // for logging
using Orchestrator.Service.Models;    // for the manifest/plan/history model classes

namespace Orchestrator.Service.Services;   // groups this with the other services

public interface ISyncService   // the contract for running a sync
{
    /// <summary>Run one full sync cycle. Never throws; failures are logged and recorded.</summary>
    Task<SyncRecord> RunSyncAsync(CancellationToken ct = default);
}

public sealed class SyncService : ISyncService   // the actual implementation
{
    private const int MaxHistoryRecords = 200;   // keep at most this many past sync records

    private readonly IGitHubClient _github;             // downloads the manifest and files
    private readonly IManifestService _manifests;       // loads state and builds the plan
    private readonly IChecksumService _checksums;       // verifies downloaded files
    private readonly IStartupManager _startup;          // handles startup registration
    private readonly IConfigService _configService;     // config + machine state
    private readonly ILogger<SyncService> _log;         // logger
    private readonly OrchestratorConfig _config;        // our settings

    public SyncService(
        IGitHubClient github,
        IManifestService manifests,
        IChecksumService checksums,
        IStartupManager startup,
        IConfigService configService,
        ILogger<SyncService> log)   // all dependencies handed in by DI
    {
        _github = github;                   // store each collaborator
        _manifests = manifests;
        _checksums = checksums;
        _startup = startup;
        _configService = configService;
        _config = configService.Config;     // grab the settings for convenience
        _log = log;
    }

    public async Task<SyncRecord> RunSyncAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();   // start timing this cycle
        var record = new SyncRecord();   // the result we'll fill in and return
        _log.LogInformation("========== SYNC CYCLE STARTED ==========");

        try
        {
            _configService.EnsureDirectories();   // make sure our working folders exist

            _log.LogInformation("Fetching manifest from GitHub...");
            var remote = await _github.GetManifestAsync(ct);   // download the latest manifest
            if (remote is null)   // download/parse failed?
            {
                record.Success = false;                                             // mark this cycle as failed
                record.Errors.Add("Manifest fetch failed; keeping current state."); // note why
                _log.LogWarning("Manifest unavailable — skipping cycle, will retry next interval");
                return Finish(record, sw);   // stop here; keep the machine as-is and try again next time
            }

            record.ManifestVersion = remote.Version;   // remember which manifest version we're applying
            _log.LogInformation("Manifest fetched (v{Version}). {Active} active, {Deleted} deleted",
                remote.Version, remote.ActivePrograms.Count(), remote.DeletedPrograms.Count());  // log a summary

            var local = _manifests.LoadLocalManifest();          // the manifest we applied last time (or null)
            var checksumCache = _manifests.LoadChecksumCache();  // remembered file fingerprints
            var plan = _manifests.BuildPlan(remote, local, checksumCache);   // work out the to-do list

            if (!plan.HasWork)   // nothing to install/update/delete?
                _log.LogInformation("Everything up to date.");

            foreach (var action in plan.Actions)   // carry out each planned action
            {
                ct.ThrowIfCancellationRequested();   // bail out promptly if the service is stopping
                try
                {
                    switch (action.Type)   // what should we do for this program?
                    {
                        case SyncActionType.Install:
                        case SyncActionType.Update:
                            await InstallAsync(action, checksumCache, ct);   // download + install (same code for both)
                            (action.Type == SyncActionType.Install ? record.Installed : record.Updated)  // record it under...
                                .Add($"{action.Program.Name} v{action.Program.Version}");                // ...installed or updated
                            break;
                        case SyncActionType.Delete:
                            DeleteProgram(action.Program, checksumCache);   // uninstall it
                            record.Deleted.Add(action.Program.Name);        // record the deletion
                            break;
                        case SyncActionType.UpToDate:
                            break;   // nothing to do
                    }
                }
                catch (Exception ex)
                {
                    var msg = $"{action.Type} {action.Program.Name}: {ex.Message}";   // build an error message
                    record.Errors.Add(msg);                                           // record it...
                    _log.LogError(ex, "Action failed: {Action}", action);             // ...and log it, but keep going with the next action
                }
            }

            // Persist new state so next cycle diffs correctly.
            _manifests.SaveChecksumCache(checksumCache);   // save the updated fingerprints
            _manifests.SaveLocalManifest(remote);          // remember this manifest as the new baseline

            record.Success = record.Errors.Count == 0;     // success only if nothing errored
        }
        catch (OperationCanceledException)
        {
            throw;   // service is stopping -> let the caller handle it
        }
        catch (Exception ex)
        {
            record.Success = false;          // unexpected failure -> mark the cycle failed
            record.Errors.Add(ex.Message);   // record the error
            _log.LogError(ex, "Sync cycle failed");
        }

        return Finish(record, sw);   // wrap up (timing, history, logs) and return the record
    }

    private SyncRecord Finish(SyncRecord record, Stopwatch sw)
    {
        sw.Stop();                                                     // stop the timer
        record.DurationSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2);   // record how long it took
        AppendHistory(record);                                         // save this record to sync-history.json
        _log.LogInformation("Sync {Status} in {Seconds}s (installed {I}, updated {U}, deleted {D}, errors {E})",
            record.Success ? "completed" : "completed with errors",
            record.DurationSeconds, record.Installed.Count, record.Updated.Count,
            record.Deleted.Count, record.Errors.Count);   // log the summary line
        _log.LogInformation("========== SYNC CYCLE COMPLETED ==========");
        return record;   // return the finished record
    }

    private async Task InstallAsync(SyncAction action, Dictionary<string, string> checksumCache, CancellationToken ct)
    {
        var p = action.Program;   // the program to install/update
        var verb = action.Type == SyncActionType.Update
            ? $"Updating {p.Name} v{action.PreviousVersion} -> v{p.Version}"   // nicer wording for updates
            : $"Installing {p.Name} v{p.Version}";                            // vs. fresh installs
        _log.LogInformation("{Verb}", verb);

        var bytes = await _github.DownloadFileAsync(p, ct);   // download the file from GitHub
        _log.LogInformation("Downloaded {Kb:N1} KB", bytes.Length / 1024.0);

        if (!_checksums.Verify(bytes, p.NormalizedChecksum))   // does it match the expected fingerprint?
        {
            var actual = _checksums.ComputeSha256(bytes);      // compute the real one for the error message
            throw new InvalidOperationException(
                $"Checksum mismatch. expected={p.NormalizedChecksum} actual={actual}");   // refuse to install a mismatched file
        }

        if (p.NormalizedChecksum is not null)
            _log.LogInformation("Checksum verified");   // good, it matched
        else
            _log.LogWarning("No checksum in manifest for {Name} — installing unverified", p.Name);   // no checksum -> warn but proceed

        Directory.CreateDirectory(p.InstallPath);   // make sure the install folder exists
        var target = p.FullFilePath;                // the final file path
        // Write to temp then move to make replacement atomic-ish and avoid partial files.
        var tmp = target + ".tmp";                          // write to a temp name first...
        await File.WriteAllBytesAsync(tmp, bytes, ct);      // ...write all the bytes there...
        File.Move(tmp, target, overwrite: true);            // ...then swap it into place in one step
        _log.LogInformation("Installed to {Path}", p.InstallPath);

        checksumCache[p.Id] = _checksums.ComputeSha256(bytes);   // remember this file's fingerprint for next time

        if (OperatingSystem.IsWindows())   // startup registration is Windows-only
        {
            // Register when startup is requested; otherwise clear any prior registration
            // (handles a program that had runAtStartup flipped off in a later manifest).
            if (p.RunAtStartup)
                _startup.Register(p);   // set it to launch at startup
            else
                _startup.Remove(p);     // make sure it's NOT set to launch at startup
        }

        if (p.RunOnce)         // marked run-once?
            MaybeRunOnce(p);   // run it now if it hasn't run on this machine yet
    }

    private void DeleteProgram(ProgramEntry p, Dictionary<string, string> checksumCache)
    {
        _log.LogInformation("Deleting {Name}{Reason}", p.Name,
            string.IsNullOrWhiteSpace(p.Reason) ? "" : $" ({p.Reason})");   // log the name and reason (if given)

        if (OperatingSystem.IsWindows())
            _startup.Remove(p);   // remove any startup registration first

        if (!string.IsNullOrWhiteSpace(p.InstallPath) && Directory.Exists(p.InstallPath))   // if its folder exists...
        {
            try { Directory.Delete(p.InstallPath, recursive: true); }   // delete the whole install folder
            catch (Exception ex) { _log.LogWarning(ex, "Could not delete {Path}", p.InstallPath); }   // warn if we can't
            _log.LogInformation("Removed {Path}", p.InstallPath);
        }

        checksumCache.Remove(p.Id);   // forget its remembered fingerprint
    }

    [SupportedOSPlatform("windows")]   // this method uses Windows-only process launching
    private void MaybeRunOnce(ProgramEntry p)
    {
        if (!OperatingSystem.IsWindows()) return;   // safety guard: do nothing off Windows

        var machine = _configService.LoadOrCreateMachineConfig();   // load this machine's state
        if (machine.CompletedRunOnce.Contains(p.Id)) return;        // already ran here? -> skip

        try
        {
            var psi = new ProcessStartInfo   // set up how to launch the program
            {
                FileName = p.FullFilePath,                 // the program to run
                Arguments = p.Arguments ?? string.Empty,   // its arguments (if any)
                UseShellExecute = true,                    // launch via the shell
                WindowStyle = ProcessWindowStyle.Hidden,   // don't show a window
                WorkingDirectory = p.InstallPath           // run from its install folder
            };
            Process.Start(psi);                            // launch it
            machine.CompletedRunOnce.Add(p.Id);            // mark it as done on this machine...
            _configService.SaveMachineConfig(machine);     // ...and persist that so it won't run again
            _log.LogInformation("Executed runOnce program {Name}", p.Name);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "runOnce launch failed for {Name}", p.Name);   // log a launch failure
        }
    }

    private void AppendHistory(SyncRecord record)
    {
        try
        {
            SyncHistory history = new();   // start with an empty history...
            if (File.Exists(_config.SyncHistoryPath))   // ...but load the existing one if present
            {
                history = JsonSerializer.Deserialize<SyncHistory>(
                    File.ReadAllText(_config.SyncHistoryPath)) ?? new();
            }
            history.Records.Add(record);   // add this cycle's record
            if (history.Records.Count > MaxHistoryRecords)   // too many records?
                history.Records.RemoveRange(0, history.Records.Count - MaxHistoryRecords);   // drop the oldest ones

            File.WriteAllText(_config.SyncHistoryPath,   // write the updated history back to disk...
                JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true }));  // ...pretty-printed
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not write sync-history.json");   // history is best-effort; just warn on failure
        }
    }
}
