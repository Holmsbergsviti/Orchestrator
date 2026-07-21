// =====================================================================================
// FILE PURPOSE (in plain terms):
//   The "brain" that decides what needs to happen. It remembers the last manifest it
//   applied (and a cache of file fingerprints) on disk, and its BuildPlan method
//   compares the fresh manifest from GitHub against that memory + what's actually on
//   disk to produce a to-do list: install this, update that, delete the other, leave
//   the rest alone. It does the deciding, not the doing.
// =====================================================================================

using System.Text.Json;                    // for reading/writing the cached JSON
using Microsoft.Extensions.Logging;        // for logging
using Orchestrator.Service.Models;         // for the manifest/plan model classes

namespace Orchestrator.Service.Services;   // groups this with the other services

public interface IManifestService   // the contract for manifest state + planning
{
    Manifest? LoadLocalManifest();                 // read the last-applied manifest from cache (or null)
    void SaveLocalManifest(Manifest manifest);     // save the just-applied manifest to cache

    Dictionary<string, string> LoadChecksumCache();          // read the id -> fingerprint cache
    void SaveChecksumCache(Dictionary<string, string> cache); // save the id -> fingerprint cache

    /// <summary>Diff remote manifest against local manifest + on-disk state to produce a plan.</summary>
    SyncPlan BuildPlan(Manifest remote, Manifest? local, Dictionary<string, string> checksumCache);
}

public sealed class ManifestService : IManifestService   // the actual implementation
{
    private static readonly JsonSerializerOptions JsonOpts = new()   // JSON read/write options
    {
        WriteIndented = true,                // pretty-print files we write
        PropertyNameCaseInsensitive = true   // ignore case when reading field names
    };

    private readonly OrchestratorConfig _config;        // our settings (cache file paths)
    private readonly ILogger<ManifestService> _log;     // logger

    public ManifestService(IConfigService configService, ILogger<ManifestService> log)  // dependencies from DI
    {
        _config = configService.Config;   // grab the settings
        _log = log;                       // store the logger
    }

    public Manifest? LoadLocalManifest()
    {
        if (!File.Exists(_config.LocalManifestPath)) return null;   // no cached manifest yet -> null
        try
        {
            return JsonSerializer.Deserialize<Manifest>(               // read + parse the cached manifest
                File.ReadAllText(_config.LocalManifestPath), JsonOpts);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Local manifest unreadable; treating as none");  // corrupt cache -> treat as none
            return null;
        }
    }

    public void SaveLocalManifest(Manifest manifest)
        => File.WriteAllText(_config.LocalManifestPath, JsonSerializer.Serialize(manifest, JsonOpts));  // write the manifest as JSON

    public Dictionary<string, string> LoadChecksumCache()
    {
        if (!File.Exists(_config.ChecksumsPath)) return new();   // no cache file -> empty dictionary
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(   // read + parse the fingerprint cache
                File.ReadAllText(_config.ChecksumsPath), JsonOpts) ?? new();  // fall back to empty if it parses to null
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Checksum cache unreadable; resetting");  // corrupt cache -> start fresh
            return new();
        }
    }

    public void SaveChecksumCache(Dictionary<string, string> cache)
        => File.WriteAllText(_config.ChecksumsPath, JsonSerializer.Serialize(cache, JsonOpts));  // write the fingerprint cache as JSON

    public SyncPlan BuildPlan(Manifest remote, Manifest? local, Dictionary<string, string> checksumCache)
    {
        var plan = new SyncPlan();   // the to-do list we're going to fill in
        var localById = (local?.Programs ?? new List<ProgramEntry>())   // index the last-applied programs by id...
            .ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase); // ...for quick lookups (case-insensitive)

        foreach (var prog in remote.ActivePrograms)   // look at every program GitHub says should be active
        {
            localById.TryGetValue(prog.Id, out var prev);   // find what we knew about it before (if anything)

            if (prev is null || prev.Status == ProgramStatus.Deleted)   // never installed, or was previously deleted...
            {
                plan.Actions.Add(new SyncAction { Type = SyncActionType.Install, Program = prog });  // -> install it
                continue;
            }

            // Version bump => update.
            if (!string.Equals(prev.Version, prog.Version, StringComparison.OrdinalIgnoreCase))  // the version changed...
            {
                plan.Actions.Add(new SyncAction
                {
                    Type = SyncActionType.Update,   // -> update it
                    Program = prog,
                    PreviousVersion = prev.Version  // remember the old version for logging
                });
                continue;
            }

            // Same version: verify the file is actually present and intact, else reinstall (repair).
            if (!IsInstalledIntact(prog, checksumCache))   // same version, but the file is missing or altered...
            {
                plan.Actions.Add(new SyncAction { Type = SyncActionType.Install, Program = prog });  // -> reinstall (repair)
                continue;
            }

            plan.Actions.Add(new SyncAction { Type = SyncActionType.UpToDate, Program = prog });  // otherwise it's already correct
        }

        // Deletions: remote status=deleted AND currently present locally.
        foreach (var prog in remote.DeletedPrograms)   // look at every program explicitly marked "deleted"
        {
            var installed = localById.TryGetValue(prog.Id, out var prev) && prev.Status == ProgramStatus.Active;  // did we have it installed?
            var onDisk = !string.IsNullOrWhiteSpace(prog.InstallPath) && Directory.Exists(prog.InstallPath);      // are its files still on disk?
            if (installed || onDisk)   // if it's present either way...
                plan.Actions.Add(new SyncAction
                {
                    Type = SyncActionType.Delete,   // -> delete it
                    Program = prog,
                    PreviousVersion = prev?.Version
                });
        }

        // Deletions: programs that vanished from the manifest entirely. If an admin
        // deletes an entry outright (rather than flipping it to status=deleted), we
        // still uninstall it, using the last-known local entry which carries the
        // installPath/name/type needed to clean up files and startup registration.
        var remoteIds = remote.Programs         // gather every id present in the remote manifest (any status)...
            .Select(p => p.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);   // ...into a fast-lookup set
        foreach (var prev in localById.Values)   // walk everything we previously knew about
        {
            if (prev.Status != ProgramStatus.Active) continue;   // already handled or never installed
            if (remoteIds.Contains(prev.Id)) continue;           // still present in remote (any status) -> skip
            plan.Actions.Add(new SyncAction
            {
                Type = SyncActionType.Delete,   // it vanished from the manifest -> uninstall it
                Program = prev,                 // use our last-known entry (it has the installPath, etc.)
                PreviousVersion = prev.Version
            });
        }

        return plan;   // hand back the finished to-do list
    }

    private static bool IsInstalledIntact(ProgramEntry prog, Dictionary<string, string> checksumCache)
    {
        if (!File.Exists(prog.FullFilePath)) return false;   // the file isn't there -> not intact
        if (prog.NormalizedChecksum is null) return true; // no checksum to compare -> assume intact
        return checksumCache.TryGetValue(prog.Id, out var cached)   // do we have a remembered fingerprint...
               && string.Equals(cached, prog.NormalizedChecksum, StringComparison.OrdinalIgnoreCase);  // ...that matches the expected one?
    }
}
