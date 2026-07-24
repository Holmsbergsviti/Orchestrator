// =====================================================================================
// FILE PURPOSE (in plain terms):
//   The brains of the console. It reads your control repo through git: the program list
//   from manifest.json, your friendly machine labels from fleet.json, and each machine's
//   heartbeat from state/<id>.json on the fleet-state branch. It combines those into one
//   view for the web page. When you save, it changes ONLY each program's "target" field
//   (and fleet.json) — everything else in the manifest is left exactly as it was — then
//   commits and pushes. Manifest edits go through a JSON DOM so no other field is lost.
// =====================================================================================

using System.Text.Json;                    // parsing/serializing
using System.Text.Json.Nodes;              // JsonNode DOM for safe manifest edits
using System.Text.Json.Serialization;      // attribute mapping for the DTOs

namespace Orchestrator.Console;

// ---- settings ------------------------------------------------------------------------

public sealed class ConsoleOptions
{
    public const string SectionName = "Console";
    public string ControlRepoPath { get; set; } = "";
    public string Remote { get; set; } = "origin";
    public string MainBranch { get; set; } = "main";
    public string FleetStateBranch { get; set; } = "fleet-state";
    public bool OpenBrowser { get; set; } = true;
}

// ---- wire models (what the web page receives / sends) --------------------------------

/// <summary>One program as shown in the console (a read-only summary + its current targeting).</summary>
public sealed class ProgramView
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Version { get; set; }
    public string Status { get; set; } = "active";
    /// <summary>True when the program applies to every machine (target omitted/"all").</summary>
    public bool AllMachines { get; set; }
    /// <summary>Machine ids/hostnames this program is explicitly targeted at (raw manifest tokens).</summary>
    public List<string> Target { get; set; } = new();
}

/// <summary>One machine as shown in the console (from its heartbeat + your label).</summary>
public sealed class MachineView
{
    public string MachineId { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string? Label { get; set; }
    public string? Os { get; set; }
    public string? AgentVersion { get; set; }
    public string? LastSeenUtc { get; set; }
    public bool Online { get; set; }
    public bool LastSyncSuccess { get; set; }
    public string? ManifestVersion { get; set; }
    public string? LastError { get; set; }
    public List<string> AppliedProgramIds { get; set; } = new();
}

public sealed class StateResponse
{
    public string GeneratedAt { get; set; } = DateTimeOffset.UtcNow.ToString("O");
    public List<ProgramView> Programs { get; set; } = new();
    public List<MachineView> Machines { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public sealed class SaveRequest
{
    [JsonPropertyName("programTargets")] public List<ProgramTarget> ProgramTargets { get; set; } = new();
    [JsonPropertyName("labels")] public Dictionary<string, string> Labels { get; set; } = new();
}

public sealed class ProgramTarget
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("all")] public bool All { get; set; }
    [JsonPropertyName("machineIds")] public List<string> MachineIds { get; set; } = new();
}

public sealed class SaveResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public string? Commit { get; set; }
}

/// <summary>The heartbeat file shape (must match the service's Heartbeat model).</summary>
internal sealed class HeartbeatFile
{
    [JsonPropertyName("machineId")] public string MachineId { get; set; } = "";
    [JsonPropertyName("hostname")] public string Hostname { get; set; } = "";
    [JsonPropertyName("os")] public string? Os { get; set; }
    [JsonPropertyName("agentVersion")] public string? AgentVersion { get; set; }
    [JsonPropertyName("lastSeenUtc")] public string? LastSeenUtc { get; set; }
    [JsonPropertyName("syncIntervalMinutes")] public int SyncIntervalMinutes { get; set; }
    [JsonPropertyName("lastSyncSuccess")] public bool LastSyncSuccess { get; set; }
    [JsonPropertyName("manifestVersion")] public string? ManifestVersion { get; set; }
    [JsonPropertyName("appliedProgramIds")] public List<string> AppliedProgramIds { get; set; } = new();
    [JsonPropertyName("lastError")] public string? LastError { get; set; }
}

// ---- service -------------------------------------------------------------------------

public sealed class ControlRepo
{
    private const string ManifestPath = "manifest.json";
    private const string FleetLabelsPath = "fleet.json";
    private const string StateDir = "state";

    private static readonly JsonSerializerOptions JsonRead = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonNodeOptions NodeOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions JsonWrite = new() { WriteIndented = true };

    private readonly ConsoleOptions _opt;
    private readonly GitRepo _git;
    private readonly ILogger<ControlRepo> _log;

    public ControlRepo(ConsoleOptions opt, ILogger<ControlRepo> log)
    {
        _opt = opt;
        _git = new GitRepo(opt.ControlRepoPath);
        _log = log;
    }

    public bool RepoIsValid() => _git.IsValid();
    public string RepoPath => _opt.ControlRepoPath;

    private string MainRef => $"{_opt.Remote}/{_opt.MainBranch}";
    private string FleetRef => $"{_opt.Remote}/{_opt.FleetStateBranch}";

    /// <summary>Fetch, then build the combined fleet + programs view for the UI.</summary>
    public async Task<StateResponse> LoadStateAsync(CancellationToken ct)
    {
        var resp = new StateResponse();
        await _git.FetchAsync(_opt.Remote, ct);

        // --- programs (from manifest.json on the main branch) ---
        var manifestText = _git.ReadFileFromRef(MainRef, ManifestPath);
        var programNodes = new List<JsonObject>();
        if (manifestText is null)
        {
            resp.Warnings.Add($"No {ManifestPath} found on {MainRef}.");
        }
        else if (JsonNode.Parse(manifestText, NodeOpts) is JsonObject root && root["programs"] is JsonArray progs)
        {
            foreach (var n in progs.OfType<JsonObject>()) programNodes.Add(n);
        }

        // --- friendly labels (from fleet.json on the main branch) ---
        var labels = LoadLabels(_git.ReadFileFromRef(MainRef, FleetLabelsPath));

        // --- heartbeats (from state/*.json on the fleet-state branch) ---
        var heartbeats = new List<HeartbeatFile>();
        var stateFiles = _git.ListDirOnRef(FleetRef, StateDir);
        if (stateFiles.Count == 0)
            resp.Warnings.Add($"No machines have reported yet (branch '{_opt.FleetStateBranch}' is empty or missing).");
        foreach (var file in stateFiles)
        {
            if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            var text = _git.ReadFileFromRef(FleetRef, file);
            if (text is null) continue;
            try
            {
                var hb = JsonSerializer.Deserialize<HeartbeatFile>(text, JsonRead);
                if (hb is not null && !string.IsNullOrWhiteSpace(hb.MachineId)) heartbeats.Add(hb);
            }
            catch (Exception ex) { _log.LogWarning(ex, "Unreadable heartbeat {File}", file); }
        }

        resp.Machines = heartbeats
            .Select(hb => ToMachineView(hb, labels))
            .OrderByDescending(m => m.Online)
            .ThenBy(m => m.Label ?? m.Hostname, StringComparer.OrdinalIgnoreCase)
            .ToList();

        resp.Programs = programNodes.Select(ToProgramView).ToList();
        return resp;
    }

    /// <summary>Apply targeting + label edits to the repo and push. Returns the outcome.</summary>
    public async Task<SaveResult> SaveAsync(SaveRequest req, CancellationToken ct)
    {
        await _git.FetchAsync(_opt.Remote, ct);
        if (!_git.IsClean())
            return new SaveResult { Ok = false, Message = "The control-repo clone has uncommitted changes. Commit or discard them, then retry." };

        try
        {
            await _git.SyncBranchToRemoteAsync(_opt.Remote, _opt.MainBranch, ct);
        }
        catch (GitException ex)
        {
            return new SaveResult { Ok = false, Message = $"Could not fast-forward '{_opt.MainBranch}' to the remote: {ex.Message}" };
        }

        var repoRoot = _opt.ControlRepoPath;
        var manifestFull = Path.Combine(repoRoot, ManifestPath);
        if (!File.Exists(manifestFull))
            return new SaveResult { Ok = false, Message = $"{ManifestPath} not found in the clone after sync." };

        // Edit the manifest as a DOM so only "target" changes; every other field is preserved.
        var root = JsonNode.Parse(File.ReadAllText(manifestFull), NodeOpts) as JsonObject
                   ?? throw new InvalidOperationException("manifest.json is not a JSON object.");
        if (root["programs"] is not JsonArray progs)
            return new SaveResult { Ok = false, Message = "manifest.json has no 'programs' array." };

        var byId = req.ProgramTargets.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var prog in progs.OfType<JsonObject>())
        {
            var id = prog["id"]?.GetValue<string>();
            if (id is null || !byId.TryGetValue(id, out var t)) continue;   // only touch programs the UI sent

            if (t.All)
            {
                prog.Remove("target");   // applies to all machines (including future ones)
            }
            else
            {
                var arr = new JsonArray();
                foreach (var mid in t.MachineIds.Distinct(StringComparer.OrdinalIgnoreCase)) arr.Add(mid);
                prog["target"] = arr;    // explicit machine-id list
            }
        }
        root["lastUpdated"] = DateTimeOffset.UtcNow.ToString("O");   // stamp the edit
        File.WriteAllText(manifestFull, root.ToJsonString(JsonWrite));

        // fleet.json: friendly labels (console-owned).
        var fleetFull = Path.Combine(repoRoot, FleetLabelsPath);
        var labelObj = new JsonObject();
        foreach (var kv in req.Labels)
            if (!string.IsNullOrWhiteSpace(kv.Value)) labelObj[kv.Key] = kv.Value;
        File.WriteAllText(fleetFull, new JsonObject { ["labels"] = labelObj }.ToJsonString(JsonWrite));

        if (_git.IsClean())
            return new SaveResult { Ok = true, Message = "No changes to save.", Commit = null };

        try
        {
            var sha = await _git.CommitAndPushAsync(
                _opt.Remote, _opt.MainBranch,
                $"console: update targeting/labels ({DateTimeOffset.UtcNow:u})",
                new[] { ManifestPath, FleetLabelsPath }, ct);
            return new SaveResult { Ok = true, Message = "Saved and pushed.", Commit = sha };
        }
        catch (GitException ex)
        {
            return new SaveResult { Ok = false, Message = $"Commit/push failed (the remote may have moved — reload and retry): {ex.Message}" };
        }
    }

    // ---- mapping helpers ---------------------------------------------------------------

    private static Dictionary<string, string> LoadLabels(string? fleetJson)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(fleetJson)) return result;
        try
        {
            if (JsonNode.Parse(fleetJson, NodeOpts) is JsonObject root && root["labels"] is JsonObject labels)
                foreach (var kv in labels)
                    if (kv.Value is not null) result[kv.Key] = kv.Value.GetValue<string>();
        }
        catch { /* malformed fleet.json -> no labels */ }
        return result;
    }

    private static ProgramView ToProgramView(JsonObject prog)
    {
        var target = ReadTarget(prog["target"]);
        var all = target is null || target.Count == 0
                  || target.Any(t => string.Equals(t, "all", StringComparison.OrdinalIgnoreCase));
        return new ProgramView
        {
            Id = prog["id"]?.GetValue<string>() ?? "",
            Name = prog["name"]?.GetValue<string>() ?? prog["id"]?.GetValue<string>() ?? "",
            Version = prog["version"]?.GetValue<string>(),
            Status = prog["status"]?.GetValue<string>() ?? "active",
            AllMachines = all,
            Target = target ?? new List<string>()
        };
    }

    /// <summary>Read the manifest "target" (string or array) into a list.</summary>
    private static List<string>? ReadTarget(JsonNode? node) => node switch
    {
        null => null,
        JsonArray arr => arr.Where(x => x is not null).Select(x => x!.GetValue<string>()).ToList(),
        JsonValue v when v.TryGetValue<string>(out var s) => new List<string> { s },
        _ => null
    };

    private static MachineView ToMachineView(HeartbeatFile hb, Dictionary<string, string> labels)
    {
        labels.TryGetValue(hb.MachineId, out var label);
        return new MachineView
        {
            MachineId = hb.MachineId,
            Hostname = hb.Hostname,
            Label = label,
            Os = hb.Os,
            AgentVersion = hb.AgentVersion,
            LastSeenUtc = hb.LastSeenUtc,
            Online = IsOnline(hb),
            LastSyncSuccess = hb.LastSyncSuccess,
            ManifestVersion = hb.ManifestVersion,
            LastError = hb.LastError,
            AppliedProgramIds = hb.AppliedProgramIds
        };
    }

    /// <summary>A machine is "online" if its last heartbeat is within ~2 sync intervals.</summary>
    private static bool IsOnline(HeartbeatFile hb)
    {
        if (!DateTimeOffset.TryParse(hb.LastSeenUtc, out var seen)) return false;
        var interval = Math.Max(1, hb.SyncIntervalMinutes);
        return DateTimeOffset.UtcNow - seen <= TimeSpan.FromMinutes(2 * interval + 2);
    }
}
