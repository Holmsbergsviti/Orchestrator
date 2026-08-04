// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Keeps the agent itself up to date. Each sync it reads agent.json from the control repo,
//   compares the published build's hash against the hash of the exe it's actually running,
//   and if they differ downloads the new build, verifies it, and hands the swap off to
//   install.ps1 via a one-time SYSTEM scheduled task.
//
//   Three things drive the shape of this file:
//
//   1. A running service cannot overwrite its own exe. So the update is not performed here —
//      it's SCHEDULED, and the task that does it runs outside this process, stops the service,
//      replaces the binary and starts it again. install.ps1 already does exactly that and now
//      preserves settings, so this doesn't reimplement any of it.
//   2. The downloaded binary runs as SYSTEM on every machine in the fleet. It is verified
//      against a hash from the PRIVATE control repo before it is allowed anywhere near the
//      install folder; a mismatch is treated as hostile, not as a retry.
//   3. Identity is the hash of the running exe, not a version number the build reports about
//      itself. A machine that was rolled back, hand-patched, or interrupted mid-update still
//      converges on the published build, and there's no version string to get out of step.
// =====================================================================================

using System.Runtime.Versioning;      // [SupportedOSPlatform]
using Microsoft.Extensions.Logging;   // logging
using Orchestrator.Service.Models;    // AgentRelease / OrchestratorConfig

namespace Orchestrator.Service.Services;

public interface ISelfUpdateService
{
    /// <summary>Check for a newer agent build and schedule the swap if there is one. Best-effort:
    /// a failure here must never stop the sync cycle that called it.</summary>
    Task CheckAndUpdateAsync(CancellationToken ct = default);
}

public sealed class SelfUpdateService : ISelfUpdateService
{
    /// <summary>How long to wait before retrying a build that failed to download or verify, so a
    /// bad release doesn't mean a download attempt every single sync on every machine.</summary>
    private static readonly TimeSpan RetryCooldown = TimeSpan.FromMinutes(30);

    private readonly IGitHubClient _github;
    private readonly IChecksumService _checksums;
    private readonly IScheduledTaskService _scheduledTasks;
    private readonly IConfigService _configService;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<SelfUpdateService> _log;
    private readonly OrchestratorConfig _config;

    private string? _lastFailedSha;      // the build we last failed on...
    private DateTimeOffset _retryAfter;  // ...and when it's worth trying again

    public SelfUpdateService(
        IGitHubClient github,
        IChecksumService checksums,
        IScheduledTaskService scheduledTasks,
        IConfigService configService,
        IHttpClientFactory httpFactory,
        ILogger<SelfUpdateService> log)
    {
        _github = github;
        _checksums = checksums;
        _scheduledTasks = scheduledTasks;
        _configService = configService;
        _httpFactory = httpFactory;
        _config = configService.Config;
        _log = log;
    }

    public async Task CheckAndUpdateAsync(CancellationToken ct = default)
    {
        if (!_config.AutoUpdate) return;
        if (!OperatingSystem.IsWindows()) return;   // the swap is a Windows service operation

        var release = await _github.GetAgentReleaseAsync(ct);
        if (release is null) return;                // no agent.json -> self-update not in use

        if (!release.IsUsable())
        {
            // Covers the deliberate brake (enabled:false) as well as a malformed entry. Both mean
            // "don't update", and neither is worth a warning every sync.
            _log.LogDebug("self-update: agent.json is present but not usable (enabled={Enabled})", release.Enabled);
            return;
        }

        var currentPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentPath))
        {
            _log.LogWarning("self-update: cannot determine the running exe's path; skipping");
            return;
        }

        var currentSha = await _checksums.ComputeSha256Async(currentPath, ct);
        if (!release.ShouldUpdate(currentSha)) return;   // already on the published build

        var wanted = release.NormalizedSha256();
        if (_lastFailedSha == wanted && DateTimeOffset.UtcNow < _retryAfter)
            return;   // this exact build already failed recently; wait before hammering it again

        _log.LogInformation("self-update: published build {Wanted} differs from the running {Current}; updating",
            Short(wanted), Short(currentSha));

        try
        {
            await DownloadVerifyAndScheduleAsync(release, wanted, ct);
        }
        catch (Exception ex)
        {
            _lastFailedSha = wanted;
            _retryAfter = DateTimeOffset.UtcNow + RetryCooldown;
            _log.LogError(ex, "self-update: failed to apply build {Wanted}; retrying after {Cooldown}", Short(wanted), RetryCooldown);
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task DownloadVerifyAndScheduleAsync(AgentRelease release, string wantedSha, CancellationToken ct)
    {
        var updateDir = Path.Combine(_config.RootPath, "update");
        Directory.CreateDirectory(updateDir);
        var stagedExe = Path.Combine(updateDir, OrchestratorDefaults.Instance.ExeName);

        // Download to memory first: a partially-written file on disk is one an installer could
        // pick up, and this is the one file whose contents must never be taken on trust.
        var client = _httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(10);   // it's a ~36 MB self-contained runtime
        var bytes = await client.GetByteArrayAsync(release.ExeUrl, ct);

        var actualSha = _checksums.ComputeSha256(bytes);
        if (!string.Equals(actualSha, wantedSha, StringComparison.OrdinalIgnoreCase))
        {
            // Refused, not retried differently: the binary that arrived is not the one the
            // control repo vouched for, and it would run as SYSTEM. Nothing is written to disk.
            throw new InvalidOperationException(
                $"Downloaded agent does not match the checksum in agent.json (expected {Short(wantedSha)}, got {Short(actualSha)}). " +
                "Refusing to install it.");
        }

        await File.WriteAllBytesAsync(stagedExe, bytes, ct);
        _log.LogInformation("self-update: staged verified build {Sha} ({Size:N0} bytes)", Short(wantedSha), bytes.Length);

        // install.ps1 lives beside the install (put there by the installer) and preserves every
        // existing setting, so the swap needs no arguments beyond where the new binary is.
        var installer = Path.Combine(_config.RootPath, "install.ps1");
        if (!File.Exists(installer))
            throw new FileNotFoundException(
                $"'{installer}' is missing, so the update can't be applied. Reinstall this machine once with " +
                "bootstrap.ps1 to place it, after which self-update is self-sufficient.", installer);

        _scheduledTasks.RunSystemOnce(
            taskName: _config.RegistryEntryPrefix + "selfupdate",
            command: "powershell.exe",
            arguments: $"-ExecutionPolicy Bypass -NoProfile -File \"{installer}\" -SourceDir \"{updateDir}\"",
            delay: TimeSpan.FromSeconds(20));   // let this sync cycle finish and the heartbeat go out

        _log.LogInformation("self-update: scheduled the swap to build {Sha}; this service will be restarted by it", Short(wantedSha));
    }

    private static string Short(string? sha)
        => string.IsNullOrWhiteSpace(sha) ? "?" : sha[..Math.Min(12, sha.Length)];
}
