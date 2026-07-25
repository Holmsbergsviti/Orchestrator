// =====================================================================================
// FILE PURPOSE (in plain terms):
//   The "gated launcher". Startup entries (registry Run value / scheduled task) don't
//   point at a program directly anymore — they point at the orchestrator with
//   "run-program <id>", which lands here. It checks the CURRENT manifest first and only
//   launches the program if it is still active and targeted at this machine. So a
//   program you deleted never runs at the next boot, even if its startup entry lingers
//   for a moment before the next sync removes it. Offline, it falls back to the last
//   synced manifest so machines still work without a network.
// =====================================================================================

using System.Diagnostics;             // to launch the program
using Microsoft.Extensions.Logging;   // for logging
using Orchestrator.Service.Models;    // for Manifest / ProgramEntry

namespace Orchestrator.Service.Services;   // groups this with the other services

public interface IProgramLauncher   // the contract for the gated launcher
{
    /// <summary>Launch a program by id, but only if it's still active + targeted here. Returns a process exit code.</summary>
    Task<int> LaunchIfActiveAsync(string programId, CancellationToken ct = default);
}

public sealed class ProgramLauncher : IProgramLauncher
{
    private readonly IGitHubClient _github;             // fetch the current manifest
    private readonly IManifestService _manifests;       // filter to this machine / read the local cache
    private readonly IConfigService _configService;     // this machine's id + hostname
    private readonly ILogger<ProgramLauncher> _log;     // logger

    public ProgramLauncher(
        IGitHubClient github, IManifestService manifests, IConfigService configService, ILogger<ProgramLauncher> log)
    {
        _github = github;
        _manifests = manifests;
        _configService = configService;
        _log = log;
    }

    public async Task<int> LaunchIfActiveAsync(string programId, CancellationToken ct = default)
    {
        var machine = _configService.LoadOrCreateMachineConfig();   // who am I

        // Prefer the CURRENT manifest so a just-deleted program is caught; fall back to the
        // last-synced local manifest (already this machine's view) if GitHub is unreachable.
        Manifest effective;
        var remote = await _github.GetManifestAsync(ct);
        if (remote is not null)
        {
            effective = _manifests.FilterForMachine(remote, machine.MachineId, machine.Hostname);
        }
        else
        {
            var local = _manifests.LoadLocalManifest();
            if (local is null)
            {
                _log.LogWarning("run-program {Id}: no manifest available (offline, no local cache) — not launching", programId);
                return 0;
            }
            _log.LogInformation("run-program {Id}: GitHub unreachable — using last-synced manifest", programId);
            effective = local;
        }

        var prog = effective.ActivePrograms
            .FirstOrDefault(p => string.Equals(p.Id, programId, StringComparison.OrdinalIgnoreCase));
        if (prog is null)
        {
            _log.LogInformation("run-program {Id}: not active/targeted on this machine — not launching", programId);
            return 0;   // deleted or retargeted away -> the whole point of the gate
        }

        if (!File.Exists(prog.FullFilePath))
        {
            _log.LogWarning("run-program {Id}: file missing at {Path} — not launching", programId, prog.FullFilePath);
            return 0;
        }

        return Launch(prog);
    }

    private int Launch(ProgramEntry program)
    {
        var cmd = LaunchCommandBuilder.Build(program);   // interpreter + args for this program type
        var psi = new ProcessStartInfo
        {
            FileName = cmd.Executable,
            Arguments = cmd.Arguments,
            UseShellExecute = false,     // run the interpreter/exe directly (no shell association)
            CreateNoWindow = true,       // no console window for our own launch
            WorkingDirectory = Directory.Exists(program.InstallPath) ? program.InstallPath : string.Empty
        };
        try
        {
            using var proc = Process.Start(psi);
            _log.LogInformation("run-program {Id}: launched {Exe} {Args}", program.Id, cmd.Executable, cmd.Arguments);
            return 0;   // fire-and-forget; we don't wait for the launched program
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "run-program {Id}: launch failed", program.Id);
            return 1;
        }
    }
}
