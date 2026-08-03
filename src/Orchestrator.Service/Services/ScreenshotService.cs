// =====================================================================================
// FILE PURPOSE (in plain terms):
//   The other half of "capture-screenshot <id>": once ScreenCaptureService has JPEG bytes,
//   this pushes them straight to the fleet-state branch (same branch/plumbing as heartbeats)
//   at screenshots/<machineId>/<id>.jpg, plus a small latest.json pointer the console reads
//   to find the newest one without having to list the whole folder. This runs INSIDE the
//   interactive user's session (see ScheduledTaskService.RunInteractiveScreenshotOnce), using
//   the exact same config/DI wiring as the "run-program" launcher — nothing here is SYSTEM-only.
// =====================================================================================

using System.Text;                    // UTF-8 encoding for the pointer JSON
using System.Text.Json;               // serializing the pointer JSON
using Microsoft.Extensions.Logging;   // logging
using Orchestrator.Service.Models;    // OrchestratorConfig

namespace Orchestrator.Service.Services;

public interface IScreenshotService
{
    /// <summary>Capture the screen and upload it for the given request id. Returns a process exit code.</summary>
    Task<int> CaptureAndUploadAsync(string requestId, CancellationToken ct = default);
}

public sealed class ScreenshotService : IScreenshotService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly IScreenCaptureService _capture;
    private readonly IGitHubClient _github;
    private readonly IConfigService _configService;
    private readonly ILogger<ScreenshotService> _log;
    private readonly OrchestratorConfig _config;

    public ScreenshotService(
        IScreenCaptureService capture,
        IGitHubClient github,
        IConfigService configService,
        ILogger<ScreenshotService> log)
    {
        _capture = capture;
        _github = github;
        _configService = configService;
        _config = configService.Config;
        _log = log;
    }

    public async Task<int> CaptureAndUploadAsync(string requestId, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            _log.LogWarning("capture-screenshot: not supported on this platform");
            return 1;
        }

        var jpeg = _capture.CaptureJpeg();
        if (jpeg is null)
        {
            _log.LogWarning("capture-screenshot {Id}: capture failed (no desktop available)", requestId);
            return 1;
        }

        try
        {
            var machine = _configService.LoadOrCreateMachineConfig();
            var branch = _config.FleetStateBranch;
            if (!await _github.EnsureBranchAsync(branch, _config.Branch, ct))
            {
                _log.LogWarning("capture-screenshot {Id}: could not ensure branch '{Branch}'", requestId, branch);
                return 1;
            }

            var imagePath = $"screenshots/{machine.MachineId}/{requestId}.jpg";
            var imageSha = await _github.GetFileShaAsync(imagePath, branch, ct);
            await _github.PutFileAsync(imagePath, jpeg, branch,
                $"screenshot: {machine.Hostname} ({machine.MachineId}) {requestId}", imageSha, ct);

            var capturedUtc = DateTimeOffset.UtcNow.ToString("O");
            var meta = JsonSerializer.Serialize(new
            {
                id = requestId,
                path = imagePath,
                capturedUtc,
                sizeBytes = jpeg.Length
            }, JsonOpts);
            var metaPath = $"screenshots/{machine.MachineId}/latest.json";
            var metaSha = await _github.GetFileShaAsync(metaPath, branch, ct);
            await _github.PutFileAsync(metaPath, Encoding.UTF8.GetBytes(meta), branch,
                $"screenshot pointer: {machine.Hostname} ({machine.MachineId}) {requestId}", metaSha, ct);

            _log.LogInformation("capture-screenshot {Id}: uploaded {Kb:N1} KB to {Path}", requestId, jpeg.Length / 1024.0, imagePath);
            return 0;
        }
        catch (GitHubWriteForbiddenException ex)
        {
            _log.LogWarning("capture-screenshot {Id}: upload refused: {Message}", requestId, ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "capture-screenshot {Id}: upload failed", requestId);
            return 1;
        }
    }
}
