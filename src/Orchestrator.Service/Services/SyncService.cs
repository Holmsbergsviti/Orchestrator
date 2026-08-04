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
    private readonly IScheduledTaskService _scheduledTasks;  // schedules interactive one-time runs (run-now, screenshot capture)
    private readonly ISelfUpdateService _selfUpdate;    // keeps the agent's own binary current
    private readonly IConfigService _configService;     // config + machine state
    private readonly IFleetReporter _fleetReporter;     // reports this machine's state back to GitHub
    private readonly ILogger<SyncService> _log;         // logger
    private readonly OrchestratorConfig _config;        // our settings

    public SyncService(
        IGitHubClient github,
        IManifestService manifests,
        IChecksumService checksums,
        IStartupManager startup,
        IScheduledTaskService scheduledTasks,
        ISelfUpdateService selfUpdate,
        IConfigService configService,
        IFleetReporter fleetReporter,
        ILogger<SyncService> log)   // all dependencies handed in by DI
    {
        _github = github;                   // store each collaborator
        _manifests = manifests;
        _checksums = checksums;
        _startup = startup;
        _scheduledTasks = scheduledTasks;
        _selfUpdate = selfUpdate;
        _configService = configService;
        _fleetReporter = fleetReporter;
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
                // Fall through to reporting/Finish so the heartbeat still shows this machine is alive.
            }
            else
            {
                record.ManifestVersion = remote.Version;   // remember which manifest version we're applying

                // Reduce the manifest to THIS machine's view: programs not targeted here are
                // presented as deleted so they get uninstalled locally (per-machine targeting).
                var machine = _configService.LoadOrCreateMachineConfig();   // this machine's id + hostname
                var effective = _manifests.FilterForMachine(remote, machine.MachineId, machine.Hostname);
                _log.LogInformation("Manifest v{Version}: {Active} active in manifest, {Mine} apply to this machine ({Host})",
                    remote.Version, remote.ActivePrograms.Count(), effective.ActivePrograms.Count(), machine.Hostname);  // log a summary

                var local = _manifests.LoadLocalManifest();          // the manifest we applied last time (or null)
                var checksumCache = _manifests.LoadChecksumCache();  // remembered file fingerprints
                var plan = _manifests.BuildPlan(effective, local, checksumCache);   // work out the to-do list for this machine

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
                                // Startup flags changed without a version bump -> re-apply the entry.
                                if (OperatingSystem.IsWindows() && action.StartupConfigChanged)
                                {
                                    if (action.Program.RunAtStartup) _startup.Register(action.Program);
                                    else _startup.Remove(action.Program);
                                    _log.LogInformation("Updated startup registration for {Name} (flags changed)", action.Program.Name);
                                }
                                break;   // otherwise nothing to do
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
                _manifests.SaveLocalManifest(effective);       // remember THIS machine's applied view as the new baseline

                // Honor any pending "run now" requests for programs active on this machine.
                foreach (var p in effective.ActivePrograms)
                    MaybeRunRequest(p);

                // Execute any pending admin command (shutdown/restart) targeted at this machine.
                await HandleCommandsAsync(machine, ct);

                record.Success = record.Errors.Count == 0;     // success only if nothing errored
            }
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

        await _fleetReporter.ReportAsync(record, ct);   // tell GitHub our state (best-effort; never throws)

        // Update the agent itself LAST: this can schedule a task that stops this very service,
        // so everything above (programs applied, commands run, heartbeat sent) needs to have
        // finished first. Never allowed to fail the cycle — a broken update must not stop a
        // machine from syncing programs.
        try { await _selfUpdate.CheckAndUpdateAsync(ct); }
        catch (Exception ex) { _log.LogWarning(ex, "Self-update check failed"); }

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

        if (p.RunOnceInstalled)     // marked run-once-on-install?
            MaybeRunOnceInstalled(p);   // run it now if it hasn't run on this machine yet
    }

    private void DeleteProgram(ProgramEntry p, Dictionary<string, string> checksumCache)
    {
        _log.LogInformation("Deleting {Name}{Reason}", p.Name,
            string.IsNullOrWhiteSpace(p.Reason) ? "" : $" ({p.Reason})");   // log the name and reason (if given)

        if (OperatingSystem.IsWindows())
        {
            _startup.Remove(p);   // remove any startup registration first
            var killed = ProcessTerminator.KillByFilePath(p.FullFilePath, _log);   // stop it now if it's still running
            if (killed > 0) _log.LogInformation("Terminated {Count} running instance(s) of {Name}", killed, p.Name);
        }

        if (!string.IsNullOrWhiteSpace(p.InstallPath) && Directory.Exists(p.InstallPath))   // if its folder exists...
        {
            try { Directory.Delete(p.InstallPath, recursive: true); }   // delete the whole install folder
            catch (Exception ex) { _log.LogWarning(ex, "Could not delete {Path}", p.InstallPath); }   // warn if we can't
            _log.LogInformation("Removed {Path}", p.InstallPath);
        }

        checksumCache.Remove(p.Id);   // forget its remembered fingerprint
    }

    [SupportedOSPlatform("windows")]   // this method uses Windows-only process launching
    private void MaybeRunOnceInstalled(ProgramEntry p)
    {
        if (!OperatingSystem.IsWindows()) return;   // safety guard: do nothing off Windows

        var machine = _configService.LoadOrCreateMachineConfig();   // load this machine's state
        if (machine.CompletedRunOnce.Contains(p.Id)) return;        // already ran here? -> skip

        try
        {
            var cmd = LaunchCommandBuilder.Build(p);   // proper interpreter per type (.ps1 -> powershell, .bat -> cmd, ...)
            var psi = new ProcessStartInfo   // set up how to launch the program
            {
                FileName = cmd.Executable,                 // the interpreter (or the exe itself)
                Arguments = cmd.Arguments,                 // the file + its arguments
                UseShellExecute = false,                   // run it directly (ShellExecute can't "run" a .ps1)
                CreateNoWindow = true,                     // no console window
                WorkingDirectory = Directory.Exists(p.InstallPath) ? p.InstallPath : string.Empty  // run from its folder if present
            };
            Process.Start(psi);                            // launch it (runs as SYSTEM; non-interactive)
            machine.CompletedRunOnce.Add(p.Id);            // mark it as done on this machine...
            _configService.SaveMachineConfig(machine);     // ...and persist that so it won't run again
            _log.LogInformation("Executed runOnceInstalled program {Name}", p.Name);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "runOnceInstalled launch failed for {Name}", p.Name);   // log a launch failure
        }
    }

    /// <summary>Honor a "run now" request: if the program's runRequest token is new for this
    /// machine, run it once in the interactive user's session, then remember the token.</summary>
    private void MaybeRunRequest(ProgramEntry p)
    {
        if (string.IsNullOrEmpty(p.RunRequest)) return;   // no pending request
        if (!OperatingSystem.IsWindows()) return;          // interactive run is Windows-only
        if (!File.Exists(p.FullFilePath)) return;          // not installed yet -> nothing to run

        var machine = _configService.LoadOrCreateMachineConfig();
        if (machine.CompletedRunRequests.TryGetValue(p.Id, out var done) && done == p.RunRequest)
            return;   // this exact request already ran here

        try
        {
            _startup.RunInteractiveOnce(p);                              // schedule the interactive one-time run
            machine.CompletedRunRequests[p.Id] = p.RunRequest;          // remember this token...
            _configService.SaveMachineConfig(machine);                  // ...so it runs only once
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "run-now failed for {Name}", p.Name);
        }
    }

    /// <summary>Run a pending admin command for this machine, plus any Wake-on-LAN requests if this is the waker.</summary>
    private async Task HandleCommandsAsync(MachineConfig machine, CancellationToken ct)
    {
        var file = await _github.GetCommandsAsync(ct);   // commands.json (optional)
        if (file is null) return;

        // This machine's own shutdown/restart command.
        if (file.Commands.TryGetValue(machine.MachineId, out var cmd)
            && !string.IsNullOrEmpty(cmd.Id) && !machine.CompletedCommands.Contains(cmd.Id))
        {
            // Record the id BEFORE acting so a delayed shutdown can't re-trigger after the machine reboots.
            machine.CompletedCommands.Add(cmd.Id);
            Trim(machine.CompletedCommands, 100);
            _configService.SaveMachineConfig(machine);
            ExecuteCommand(cmd);
        }

        // A pending screenshot capture request for this machine. The service itself has no
        // desktop (session 0), so this only schedules the actual capture into the logged-on
        // user's interactive session; that one-time run uploads the image itself.
        if (OperatingSystem.IsWindows()
            && file.Screenshots.TryGetValue(machine.MachineId, out var shot)
            && !string.IsNullOrEmpty(shot.Id) && !machine.CompletedScreenshots.Contains(shot.Id))
        {
            // Record the id BEFORE scheduling so a retried cycle can't schedule it twice.
            machine.CompletedScreenshots.Add(shot.Id);
            Trim(machine.CompletedScreenshots, 100);
            _configService.SaveMachineConfig(machine);
            _scheduledTasks.RunInteractiveScreenshotOnce(shot.Id);
        }

        // A pending live remote-control session request for this machine. Same schedule-into-
        // the-interactive-session pattern as screenshots, but the process it launches runs for
        // the whole session instead of a single capture.
        if (OperatingSystem.IsWindows()
            && file.RemoteSessions.TryGetValue(machine.MachineId, out var session)
            && !string.IsNullOrEmpty(session.Id) && !machine.CompletedRemoteSessions.Contains(session.Id))
        {
            machine.CompletedRemoteSessions.Add(session.Id);
            Trim(machine.CompletedRemoteSessions, 100);
            _configService.SaveMachineConfig(machine);

            var nowUtc = DateTimeOffset.UtcNow;
            var expired = DateTimeOffset.TryParse(session.ExpiresUtc, out var expiresUtc) && expiresUtc < nowUtc;
            if (!expired)
                // Size the task's kill-switch for a fully renewed session, not a single grant —
                // otherwise Task Scheduler would terminate a session the operator legitimately extended.
                _scheduledTasks.RunInteractiveRemoteSessionOnce(session.Id, _config.RemoteSessionAbsoluteMax);
            else
                // Print BOTH clocks. The usual cause isn't a slow sync, it's this machine's clock
                // disagreeing with the console's about what time it is in UTC — which is invisible
                // unless the two numbers sit side by side.
                _log.LogWarning(
                    "remote-session '{Id}' request expired before this sync; skipping. Deadline was {Deadline:u}, " +
                    "this machine thinks UTC is now {NowUtc:u} ({Behind} past the deadline). If that gap looks like " +
                    "whole hours, this machine's clock or time zone is wrong, not the request.",
                    session.Id, expiresUtc, nowUtc, nowUtc - expiresUtc);
        }

        // Wake-on-LAN requests are sent by the designated always-on waker (targets are powered off).
        if (_config.IsWaker && file.Wake.Count > 0)
        {
            var sentAny = false;
            foreach (var wr in file.Wake)
            {
                if (string.IsNullOrEmpty(wr.Id) || machine.CompletedWakes.Contains(wr.Id)) continue;
                if (WakeSender.SendMagicPacket(wr.Mac, _log))
                {
                    machine.CompletedWakes.Add(wr.Id);
                    sentAny = true;
                }
            }
            if (sentAny)
            {
                Trim(machine.CompletedWakes, 1000);
                _configService.SaveMachineConfig(machine);
            }
        }
    }

    private static void Trim(List<string> list, int max)
    {
        if (list.Count > max) list.RemoveRange(0, list.Count - max);
    }

    private void ExecuteCommand(MachineCommand cmd)
    {
        if (!OperatingSystem.IsWindows()) return;   // shutdown.exe is Windows-only

        // /f force-closes apps so an open program can't block/cancel it (unattended fleet use);
        // a short delay lets this sync cycle finish cleanly.
        var args = cmd.Action.Trim().ToLowerInvariant() switch
        {
            "shutdown" => "/s /f /t 5 /c \"Remote shutdown requested via Orchestrator\"",
            "restart"  => "/r /f /t 5 /c \"Remote restart requested via Orchestrator\"",
            _ => null
        };
        if (args is null)
        {
            _log.LogWarning("Ignoring unknown command action '{Action}' (id {Id})", cmd.Action, cmd.Id);
            return;
        }

        try
        {
            _log.LogWarning("Executing remote {Action} command (id {Id}) in 15s", cmd.Action, cmd.Id);
            Process.Start(new ProcessStartInfo("shutdown.exe", args) { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to execute {Action} command", cmd.Action);
        }
    }

    private void AppendHistory(SyncRecord record)
    {
        try
        {
            SyncHistory history = new();   // start with an empty history...
            if (File.Exists(_config.SyncHistoryPath))   // ...but load the existing one if present
            {
                // A corrupt/partial file (e.g. null bytes from an unclean shutdown) shouldn't
                // break history forever — just start fresh and overwrite it this cycle.
                try { history = JsonSerializer.Deserialize<SyncHistory>(File.ReadAllText(_config.SyncHistoryPath)) ?? new(); }
                catch (Exception ex) { _log.LogWarning(ex, "sync-history.json unreadable; resetting it"); history = new(); }
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
