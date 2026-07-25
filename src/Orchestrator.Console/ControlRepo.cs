// =====================================================================================
// FILE PURPOSE (in plain terms):
//   The brains of the console. It reads your control repo through git: the program list
//   from manifest.json, your friendly machine labels from fleet.json, and each machine's
//   heartbeat from state/<id>.json on the fleet-state branch. It combines those into one
//   view for the web page. When you save, it changes ONLY each program's "target" field
//   (and fleet.json) — everything else in the manifest is left exactly as it was — then
//   commits and pushes. Manifest edits go through a JSON DOM so no other field is lost.
// =====================================================================================

using System.Security.Cryptography;        // SHA256 for imported program files
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
    // Editable settings (shown pre-filled in the console).
    public string? Type { get; set; }
    public string? InstallPath { get; set; }
    public string? Arguments { get; set; }
    public string? Description { get; set; }
    public bool RunAtStartup { get; set; }
    public bool RunAsAdmin { get; set; }
    public bool RunOnceInstalled { get; set; }
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
    /// <summary>Optional per-program setting edits (null = leave settings untouched).</summary>
    [JsonPropertyName("settings")] public ProgramSettings? Settings { get; set; }
}

/// <summary>Editable manifest fields for an existing program (all optional).</summary>
public sealed class ProgramSettings
{
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("installPath")] public string? InstallPath { get; set; }
    [JsonPropertyName("arguments")] public string? Arguments { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("runAtStartup")] public bool? RunAtStartup { get; set; }
    [JsonPropertyName("runAsAdmin")] public bool? RunAsAdmin { get; set; }
    [JsonPropertyName("runOnceInstalled")] public bool? RunOnceInstalled { get; set; }
}

public sealed class SaveResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public string? Commit { get; set; }
}

/// <summary>Request to run a single program now (on-demand).</summary>
public sealed class RunNowRequest
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
}

/// <summary>Request to add a new program to the manifest.</summary>
public sealed class AddRequest
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "1.0";
    [JsonPropertyName("type")] public string Type { get; set; } = "exe";
    [JsonPropertyName("description")] public string? Description { get; set; }
    /// <summary>"import" = copy a local file into the repo; "path" = reference a file already in the repo.</summary>
    [JsonPropertyName("sourceMode")] public string SourceMode { get; set; } = "import";
    [JsonPropertyName("localFilePath")] public string? LocalFilePath { get; set; }
    [JsonPropertyName("repoPath")] public string? RepoPath { get; set; }
    [JsonPropertyName("fileName")] public string? FileName { get; set; }
    [JsonPropertyName("installPath")] public string? InstallPath { get; set; }
    [JsonPropertyName("arguments")] public string? Arguments { get; set; }
    [JsonPropertyName("runAtStartup")] public bool RunAtStartup { get; set; }
    [JsonPropertyName("runAsAdmin")] public bool RunAsAdmin { get; set; }
    [JsonPropertyName("runOnceInstalled")] public bool RunOnceInstalled { get; set; }
    [JsonPropertyName("all")] public bool All { get; set; } = true;
    [JsonPropertyName("machineIds")] public List<string> MachineIds { get; set; } = new();
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
        var heartbeats = ReadHeartbeats();
        if (heartbeats.Count == 0)
            resp.Warnings.Add($"No machines have reported yet (branch '{_opt.FleetStateBranch}' is empty or missing).");

        resp.Machines = heartbeats
            .Select(hb => ToMachineView(hb, labels))
            .OrderByDescending(m => m.Online)
            .ThenBy(m => m.Label ?? m.Hostname, StringComparer.OrdinalIgnoreCase)
            .ToList();

        resp.Programs = programNodes.Select(ToProgramView).ToList();
        return resp;
    }

    /// <summary>Apply targeting/activation + label edits to the repo and push. Returns the outcome.</summary>
    public async Task<SaveResult> SaveAsync(SaveRequest req, CancellationToken ct)
    {
        var err = await PrepareCleanMainAsync(ct);
        if (err is not null) return Fail(err);

        var manifestFull = Path.Combine(_opt.ControlRepoPath, ManifestPath);
        if (!File.Exists(manifestFull)) return Fail($"{ManifestPath} not found in the clone after sync.");

        // Edit the manifest as a DOM so only the fields we touch change; the rest is preserved.
        var root = JsonNode.Parse(File.ReadAllText(manifestFull), NodeOpts) as JsonObject
                   ?? throw new InvalidOperationException("manifest.json is not a JSON object.");
        if (root["programs"] is not JsonArray progs)
            return Fail("manifest.json has no 'programs' array.");

        var hostById = HostnamesById();   // machine id -> hostname (for readable targets)
        var byId = req.ProgramTargets.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var prog in progs.OfType<JsonObject>())
        {
            var id = prog["id"]?.GetValue<string>();
            if (id is null || !byId.TryGetValue(id, out var t)) continue;   // only touch programs the UI sent
            ApplyTargeting(prog, t, hostById);
        }
        root["lastUpdated"] = DateTimeOffset.UtcNow.ToString("O");   // stamp the edit
        File.WriteAllText(manifestFull, root.ToJsonString(JsonWrite));

        // fleet.json: friendly labels (console-owned).
        var fleetFull = Path.Combine(_opt.ControlRepoPath, FleetLabelsPath);
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
                $"console: update programs/targeting/labels ({DateTimeOffset.UtcNow:u})",
                new[] { ManifestPath, FleetLabelsPath }, ct);
            return new SaveResult { Ok = true, Message = "Saved and pushed.", Commit = sha };
        }
        catch (GitException ex)
        {
            return Fail($"Commit/push failed (the remote may have moved — reload and retry): {ex.Message}");
        }
    }

    /// <summary>Add a new program to the manifest (optionally importing a local file) and push.</summary>
    public async Task<SaveResult> AddProgramAsync(AddRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) return Fail("Program name is required.");
        var validTypes = new[] { "exe", "batch", "powershell", "vbs", "python" };
        if (!validTypes.Contains(req.Type, StringComparer.OrdinalIgnoreCase))
            return Fail($"Type must be one of: {string.Join(", ", validTypes)}.");

        var err = await PrepareCleanMainAsync(ct);
        if (err is not null) return Fail(err);

        var repoRoot = _opt.ControlRepoPath;
        var manifestFull = Path.Combine(repoRoot, ManifestPath);
        if (!File.Exists(manifestFull)) return Fail($"{ManifestPath} not found.");
        var root = JsonNode.Parse(File.ReadAllText(manifestFull), NodeOpts) as JsonObject
                   ?? throw new InvalidOperationException("manifest.json is not a JSON object.");
        if (root["programs"] is not JsonArray progs) return Fail("manifest.json has no 'programs' array.");

        var existing = progs.OfType<JsonObject>()
            .Select(p => p["id"]?.GetValue<string>()).Where(x => x is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;
        var slug = Slug(req.Name);
        var id = UniqueId(slug, existing!);
        var version = string.IsNullOrWhiteSpace(req.Version) ? "1.0" : req.Version.Trim();

        // Resolve the file: import a local file into the repo, or reference an existing repo path.
        string path, fileName;
        string? checksum = null;
        var toCommit = new List<string> { ManifestPath };
        if (string.Equals(req.SourceMode, "path", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(req.RepoPath)) return Fail("Repo path is required.");
            path = req.RepoPath.TrimStart('/');
            fileName = string.IsNullOrWhiteSpace(req.FileName) ? Path.GetFileName(path) : req.FileName!.Trim();
            var full = Path.Combine(repoRoot, path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full)) checksum = "sha256:" + Sha256Hex(full);
        }
        else   // import a local file
        {
            if (string.IsNullOrWhiteSpace(req.LocalFilePath) || !File.Exists(req.LocalFilePath))
                return Fail($"Local file not found: {req.LocalFilePath ?? "<empty>"}");
            fileName = string.IsNullOrWhiteSpace(req.FileName) ? Path.GetFileName(req.LocalFilePath!) : req.FileName!.Trim();
            path = $"programs/{slug}/v{version}/{fileName}";
            var dest = Path.Combine(repoRoot, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(req.LocalFilePath!, dest, overwrite: true);
            checksum = "sha256:" + Sha256Hex(dest);
            toCommit.Add(path);
        }

        var entry = new JsonObject
        {
            ["id"] = id,
            ["name"] = req.Name.Trim(),
            ["description"] = req.Description?.Trim() ?? "",
            ["version"] = version,
            ["status"] = "active",
            ["type"] = req.Type.ToLowerInvariant(),
            ["path"] = path,
            ["installPath"] = string.IsNullOrWhiteSpace(req.InstallPath) ? $@"C:\Windows\Orch\programs\{slug}" : req.InstallPath!.Trim(),
            ["fileName"] = fileName,
            ["runAtStartup"] = req.RunAtStartup,
            ["runAsAdmin"] = req.RunAsAdmin,
            ["runOnceInstalled"] = req.RunOnceInstalled
        };
        if (checksum is not null) entry["checksum"] = checksum;
        if (!string.IsNullOrWhiteSpace(req.Arguments)) entry["arguments"] = req.Arguments.Trim();
        if (!req.All)   // targeted at specific machines from the start
        {
            var hostById = HostnamesById();
            var tokens = req.MachineIds.Select(mid => hostById.TryGetValue(mid, out var h) ? h : mid)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (tokens.Count == 1) entry["target"] = JsonValue.Create(tokens[0]);
            else if (tokens.Count > 1) { var arr = new JsonArray(); foreach (var tk in tokens) arr.Add(tk); entry["target"] = arr; }
        }

        progs.Add(entry);
        root["lastUpdated"] = DateTimeOffset.UtcNow.ToString("O");
        File.WriteAllText(manifestFull, root.ToJsonString(JsonWrite));

        try
        {
            var sha = await _git.CommitAndPushAsync(
                _opt.Remote, _opt.MainBranch, $"console: add program {id} ({DateTimeOffset.UtcNow:u})", toCommit, ct);
            return new SaveResult { Ok = true, Message = $"Added {id}.", Commit = sha };
        }
        catch (GitException ex) { return Fail($"Commit/push failed: {ex.Message}"); }
    }

    /// <summary>Trigger a one-time interactive "run now" by rotating the program's runRequest token, then push.</summary>
    public async Task<SaveResult> RunNowAsync(string programId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(programId)) return Fail("Program id is required.");
        var err = await PrepareCleanMainAsync(ct);
        if (err is not null) return Fail(err);

        var manifestFull = Path.Combine(_opt.ControlRepoPath, ManifestPath);
        if (!File.Exists(manifestFull)) return Fail($"{ManifestPath} not found.");
        var root = JsonNode.Parse(File.ReadAllText(manifestFull), NodeOpts) as JsonObject
                   ?? throw new InvalidOperationException("manifest.json is not a JSON object.");
        if (root["programs"] is not JsonArray progs) return Fail("manifest.json has no 'programs' array.");

        var prog = progs.OfType<JsonObject>()
            .FirstOrDefault(p => string.Equals(p["id"]?.GetValue<string>(), programId, StringComparison.OrdinalIgnoreCase));
        if (prog is null) return Fail($"Program '{programId}' not found.");

        var token = DateTimeOffset.UtcNow.ToString("O");   // a fresh token = one new run on each targeted machine
        prog["runRequest"] = token;
        root["lastUpdated"] = token;
        File.WriteAllText(manifestFull, root.ToJsonString(JsonWrite));

        try
        {
            var sha = await _git.CommitAndPushAsync(_opt.Remote, _opt.MainBranch,
                $"console: run-now {programId} ({DateTimeOffset.UtcNow:u})", new[] { ManifestPath }, ct);
            return new SaveResult { Ok = true, Message = $"Run-now requested — it runs on each targeted machine's next sync.", Commit = sha };
        }
        catch (GitException ex) { return Fail($"Commit/push failed: {ex.Message}"); }
    }

    // ---- edit helpers ------------------------------------------------------------------

    /// <summary>Fetch, verify the clone is clean, and fast-forward main. Returns an error message or null.</summary>
    private async Task<string?> PrepareCleanMainAsync(CancellationToken ct)
    {
        await _git.FetchAsync(_opt.Remote, ct);
        if (!_git.IsClean())
            return "The control-repo clone has uncommitted changes. Commit or discard them, then retry.";
        try { await _git.SyncBranchToRemoteAsync(_opt.Remote, _opt.MainBranch, ct); }
        catch (GitException ex) { return $"Could not fast-forward '{_opt.MainBranch}' to the remote: {ex.Message}"; }
        return null;
    }

    /// <summary>
    /// Set a program's status + target from a UI selection: any selection (All, or one or more
    /// machines) => active; no selection => deleted (so ticking a deleted program re-activates it).
    /// </summary>
    private static void ApplyTargeting(JsonObject prog, ProgramTarget t, Dictionary<string, string> hostById)
    {
        if (t.Settings is not null) ApplySettings(prog, t.Settings);   // per-program flag/field edits

        var active = t.All || t.MachineIds.Count > 0;
        if (!active)
        {
            prog["status"] = "deleted";                                   // no machines -> remove everywhere
            prog.Remove("target");
            prog["deletedDate"] = DateTimeOffset.UtcNow.ToString("O");
            if (prog["reason"] is null) prog["reason"] = "Deactivated via console";
            return;
        }

        prog["status"] = "active";                                        // (re)activate
        prog.Remove("deletedDate");
        prog.Remove("reason");
        if (t.All)
        {
            prog.Remove("target");                                        // all machines (incl. future ones)
        }
        else
        {
            var tokens = t.MachineIds
                .Select(mid => hostById.TryGetValue(mid, out var host) ? host : mid)   // id -> hostname
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var arr = new JsonArray();
            foreach (var tok in tokens) arr.Add(tok);
            prog["target"] = tokens.Count == 1 ? JsonValue.Create(tokens[0]) : arr;
        }
    }

    /// <summary>Write the editable manifest fields from a settings edit (only fields the UI provided).</summary>
    private static void ApplySettings(JsonObject prog, ProgramSettings s)
    {
        if (s.RunAtStartup is bool ras) prog["runAtStartup"] = ras;
        if (s.RunAsAdmin is bool raa) prog["runAsAdmin"] = raa;
        if (s.RunOnceInstalled is bool ro) prog["runOnceInstalled"] = ro;
        prog.Remove("runOnce");   // drop the legacy key name if present
        if (!string.IsNullOrWhiteSpace(s.Version)) prog["version"] = s.Version!.Trim();
        if (!string.IsNullOrWhiteSpace(s.Type)) prog["type"] = s.Type!.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(s.InstallPath)) prog["installPath"] = s.InstallPath!.Trim();
        SetOrRemove(prog, "arguments", s.Arguments);
        SetOrRemove(prog, "description", s.Description);
    }

    private static void SetOrRemove(JsonObject o, string key, string? val)
    {
        if (string.IsNullOrWhiteSpace(val)) o.Remove(key);
        else o[key] = val.Trim();
    }

    private static SaveResult Fail(string message) => new() { Ok = false, Message = message };

    private static string Slug(string name)
    {
        var s = new string(name.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        while (s.Contains("--")) s = s.Replace("--", "-");
        s = s.Trim('-');
        return string.IsNullOrEmpty(s) ? "program" : s;
    }

    private static string UniqueId(string slug, HashSet<string> existing)
    {
        for (var i = 1; i < 1000; i++)
        {
            var id = $"{slug}-{i:000}";
            if (!existing.Contains(id)) return id;
        }
        return $"{slug}-{Guid.NewGuid():N}";
    }

    private static string Sha256Hex(string filePath)
    {
        using var fs = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    // ---- mapping helpers ---------------------------------------------------------------

    /// <summary>Read all heartbeat files from the fleet-state branch.</summary>
    private List<HeartbeatFile> ReadHeartbeats()
    {
        var heartbeats = new List<HeartbeatFile>();
        foreach (var file in _git.ListDirOnRef(FleetRef, StateDir))
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
        return heartbeats;
    }

    /// <summary>machine id -> hostname, from the current heartbeats (for writing readable targets).</summary>
    private Dictionary<string, string> HostnamesById()
        => ReadHeartbeats()
            .Where(h => !string.IsNullOrWhiteSpace(h.Hostname))
            .GroupBy(h => h.MachineId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Hostname, StringComparer.OrdinalIgnoreCase);

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
            Id = GetStr(prog, "id") ?? "",
            Name = GetStr(prog, "name") ?? GetStr(prog, "id") ?? "",
            Version = GetStr(prog, "version"),
            Status = GetStr(prog, "status") ?? "active",
            AllMachines = all,
            Target = target ?? new List<string>(),
            Type = GetStr(prog, "type"),
            InstallPath = GetStr(prog, "installPath"),
            Arguments = GetStr(prog, "arguments"),
            Description = GetStr(prog, "description"),
            RunAtStartup = GetBool(prog, "runAtStartup"),
            RunAsAdmin = GetBool(prog, "runAsAdmin"),
            RunOnceInstalled = GetBool(prog, "runOnceInstalled")
        };
    }

    private static bool GetBool(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    private static string? GetStr(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

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
