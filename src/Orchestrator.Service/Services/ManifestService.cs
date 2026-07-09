using System.Text.Json;
using Microsoft.Extensions.Logging;
using Orchestrator.Service.Models;

namespace Orchestrator.Service.Services;

public interface IManifestService
{
    Manifest? LoadLocalManifest();
    void SaveLocalManifest(Manifest manifest);

    Dictionary<string, string> LoadChecksumCache();
    void SaveChecksumCache(Dictionary<string, string> cache);

    /// <summary>Diff remote manifest against local manifest + on-disk state to produce a plan.</summary>
    SyncPlan BuildPlan(Manifest remote, Manifest? local, Dictionary<string, string> checksumCache);
}

public sealed class ManifestService : IManifestService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly OrchestratorConfig _config;
    private readonly ILogger<ManifestService> _log;

    public ManifestService(IConfigService configService, ILogger<ManifestService> log)
    {
        _config = configService.Config;
        _log = log;
    }

    public Manifest? LoadLocalManifest()
    {
        if (!File.Exists(_config.LocalManifestPath)) return null;
        try
        {
            return JsonSerializer.Deserialize<Manifest>(
                File.ReadAllText(_config.LocalManifestPath), JsonOpts);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Local manifest unreadable; treating as none");
            return null;
        }
    }

    public void SaveLocalManifest(Manifest manifest)
        => File.WriteAllText(_config.LocalManifestPath, JsonSerializer.Serialize(manifest, JsonOpts));

    public Dictionary<string, string> LoadChecksumCache()
    {
        if (!File.Exists(_config.ChecksumsPath)) return new();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(_config.ChecksumsPath), JsonOpts) ?? new();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Checksum cache unreadable; resetting");
            return new();
        }
    }

    public void SaveChecksumCache(Dictionary<string, string> cache)
        => File.WriteAllText(_config.ChecksumsPath, JsonSerializer.Serialize(cache, JsonOpts));

    public SyncPlan BuildPlan(Manifest remote, Manifest? local, Dictionary<string, string> checksumCache)
    {
        var plan = new SyncPlan();
        var localById = (local?.Programs ?? new List<ProgramEntry>())
            .ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var prog in remote.ActivePrograms)
        {
            localById.TryGetValue(prog.Id, out var prev);

            if (prev is null || prev.Status == ProgramStatus.Deleted)
            {
                plan.Actions.Add(new SyncAction { Type = SyncActionType.Install, Program = prog });
                continue;
            }

            // Version bump => update.
            if (!string.Equals(prev.Version, prog.Version, StringComparison.OrdinalIgnoreCase))
            {
                plan.Actions.Add(new SyncAction
                {
                    Type = SyncActionType.Update,
                    Program = prog,
                    PreviousVersion = prev.Version
                });
                continue;
            }

            // Same version: verify the file is actually present and intact, else reinstall (repair).
            if (!IsInstalledIntact(prog, checksumCache))
            {
                plan.Actions.Add(new SyncAction { Type = SyncActionType.Install, Program = prog });
                continue;
            }

            plan.Actions.Add(new SyncAction { Type = SyncActionType.UpToDate, Program = prog });
        }

        // Deletions: remote status=deleted AND currently present locally.
        foreach (var prog in remote.DeletedPrograms)
        {
            var installed = localById.TryGetValue(prog.Id, out var prev) && prev.Status == ProgramStatus.Active;
            var onDisk = !string.IsNullOrWhiteSpace(prog.InstallPath) && Directory.Exists(prog.InstallPath);
            if (installed || onDisk)
                plan.Actions.Add(new SyncAction
                {
                    Type = SyncActionType.Delete,
                    Program = prog,
                    PreviousVersion = prev?.Version
                });
        }

        return plan;
    }

    private static bool IsInstalledIntact(ProgramEntry prog, Dictionary<string, string> checksumCache)
    {
        if (!File.Exists(prog.FullFilePath)) return false;
        if (prog.NormalizedChecksum is null) return true; // no checksum to compare
        return checksumCache.TryGetValue(prog.Id, out var cached)
               && string.Equals(cached, prog.NormalizedChecksum, StringComparison.OrdinalIgnoreCase);
    }
}
