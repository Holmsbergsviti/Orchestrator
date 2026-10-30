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
using System.Text;
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
    /// <summary>Shared secret required to sign in. REQUIRED if Urls binds to anything but
    /// localhost — the console refuses to start non-locally without one.</summary>
    public string AccessToken { get; set; } = "";
    /// <summary>Path to a PFX certificate for HTTPS/WSS. REQUIRED if Urls binds non-locally.</summary>
    public string CertPfxPath { get; set; } = "";
    public string CertPfxPassword { get; set; } = "";
    /// <summary>Where the operator's private signing key lives. Blank = per-user app data
    /// (see ControlSigning.DefaultKeyPath). Never inside a repo.</summary>
    public string SigningKeyPath { get; set; } = "";
    /// <summary>The clone of the CODE repo the console runs from, for the Update button. Blank =
    /// found by walking up from where the console is running.</summary>
    public string CodeRepoPath { get; set; } = "";
    /// <summary>The ws(s):// address PCs should dial for live screen sessions, if it isn't simply this
    /// computer's name and the port in <c>Urls</c> (say, a VPN name). Blank = work it out. See RelayAdvert.</summary>
    public string RelayAdvertiseUrl { get; set; } = "";
    /// <summary>Start a free Cloudflare quick tunnel so PCs on OTHER networks can reach the relay without
    /// this computer's address being shared. Needs cloudflared installed. See TunnelService.</summary>
    public bool Tunnel { get; set; }
    /// <summary>The cloudflared program (a full path if it isn't on PATH).</summary>
    public string TunnelCommand { get; set; } = "cloudflared";
    /// <summary>A private loopback-only port the tunnel points at. It serves the agent relay path and
    /// nothing else; it must differ from the port in <c>Urls</c>.</summary>
    public int TunnelPort { get; set; } = 5081;
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
    /// <summary>SHA-256 of the exe this machine is running, so the UI can tell which machines
    /// have picked up the published build and which are lagging or stuck.</summary>
    public string? AgentSha256 { get; set; }
    public string? LastSeenUtc { get; set; }
    public bool Online { get; set; }
    public bool LastSyncSuccess { get; set; }
    public string? ManifestVersion { get; set; }
    public string? LastError { get; set; }
    public List<string> AppliedProgramIds { get; set; } = new();
    public string? Mac { get; set; }   // primary NIC MAC, for Wake-on-LAN
    public string? LastScreenshotUtc { get; set; }   // when the newest captured screenshot was taken, if any
    /// <summary>Whether this machine can be trusted for remote control: "trusted", "missing"
    /// (agent too old to sign), "changed" (key differs from the pinned one), or "error" (the
    /// console couldn't read its pin file). Anything but "trusted" means Remote is refused.</summary>
    public string RelayKey { get; set; } = "missing";
}

public sealed class StateResponse
{
    /// <summary>SHA-256 of the agent build published in agent.json, or null when self-update
    /// isn't set up. The page compares each machine against this rather than against "newest
    /// seen", so a fleet where every machine is equally out of date still reads as out of date.</summary>
    public string? ExpectedAgentSha256 { get; set; }

    public string GeneratedAt { get; set; } = DateTimeOffset.UtcNow.ToString("O");
    public List<ProgramView> Programs { get; set; } = new();
    public List<MachineView> Machines { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    /// <summary>Signed-mode status, so the page can offer "Sign" when it's needed.</summary>
    public SigningView Signing { get; set; } = new();
}

public sealed class SigningView
{
    /// <summary>Whether this computer holds the operator's signing key.</summary>
    public bool KeyPresent { get; set; }
    /// <summary>Control files on main that aren't validly signed, with who changed them last.</summary>
    public List<UnsignedFile> Unsigned { get; set; } = new();
}

public sealed class UnsignedFile
{
    public string Path { get; set; } = "";
    public string State { get; set; } = "";        // "missing" | "invalid"
    public string? LastChange { get; set; }        // "<author>, <when>" of the last commit touching it
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

/// <summary>Request to send an admin command (shutdown/restart) to a machine.</summary>
public sealed class CommandRequest
{
    [JsonPropertyName("machineId")] public string MachineId { get; set; } = "";
    [JsonPropertyName("action")] public string Action { get; set; } = "";
}

/// <summary>Request to take a machine off the console's list.</summary>
public sealed class ForgetRequest
{
    [JsonPropertyName("machineId")] public string MachineId { get; set; } = "";
}

/// <summary>Request to Wake-on-LAN one or more machines.</summary>
public sealed class WakeApiRequest
{
    [JsonPropertyName("machineIds")] public List<string> MachineIds { get; set; } = new();
}

/// <summary>Request to capture a screenshot on a machine.</summary>
public sealed class ScreenshotApiRequest
{
    [JsonPropertyName("machineId")] public string MachineId { get; set; } = "";
}

/// <summary>Result of queueing a remote-control session — includes the session id the
/// browser needs to open the matching /relay/view connection.</summary>
public sealed class RemoteSessionResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public string? Commit { get; set; }
    public string? SessionId { get; set; }
    /// <summary>True when SessionId refers to a session that was ALREADY running for this machine
    /// rather than one just queued — the UI reopens that viewer instead of starting a second one.</summary>
    public bool AlreadyRunning { get; set; }
}

/// <summary>The screenshot pointer file shape (screenshots/&lt;machineId&gt;/latest.json).</summary>
internal sealed class ScreenshotMeta
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("capturedUtc")] public string? CapturedUtc { get; set; }
    [JsonPropertyName("sizeBytes")] public long SizeBytes { get; set; }
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
    [JsonPropertyName("agentSha256")] public string? AgentSha256 { get; set; }
    [JsonPropertyName("lastSeenUtc")] public string? LastSeenUtc { get; set; }
    [JsonPropertyName("syncIntervalMinutes")] public int SyncIntervalMinutes { get; set; }
    [JsonPropertyName("heartbeatMaxIntervalMinutes")] public int HeartbeatMaxIntervalMinutes { get; set; }   // 0 = older agent
    [JsonPropertyName("lastSyncSuccess")] public bool LastSyncSuccess { get; set; }
    [JsonPropertyName("manifestVersion")] public string? ManifestVersion { get; set; }
    [JsonPropertyName("appliedProgramIds")] public List<string> AppliedProgramIds { get; set; } = new();
    [JsonPropertyName("lastError")] public string? LastError { get; set; }
    [JsonPropertyName("macAddresses")] public List<string> MacAddresses { get; set; } = new();
    [JsonPropertyName("relayPublicKey")] public string? RelayPublicKey { get; set; }   // null = older agent
}

// ---- service -------------------------------------------------------------------------

public sealed class ControlRepo
{
    private const string ManifestPath = "manifest.json";
    private const string FleetLabelsPath = "fleet.json";
    private const string StateDir = "state";

    /// <summary>How long a queued remote-control session stays valid for the agent to notice it.</summary>
    private static readonly TimeSpan PickupWindow = TimeSpan.FromMinutes(10);

    // How long the other one-shot requests stay valid. The agent skips (and consumes) any
    // request it first sees after its expiresUtc, so a machine that comes back days later,
    // or is reinstalled and has forgotten which ids it already ran, doesn't act on it.
    // A restart/shutdown is still wanted when a machine that was briefly offline returns;
    // a screenshot or wake is only useful while the operator is waiting for it.
    private static readonly TimeSpan CommandValidFor = TimeSpan.FromHours(24);
    private static readonly TimeSpan UninstallValidFor = TimeSpan.FromHours(1);
    private static readonly TimeSpan ScreenshotValidFor = TimeSpan.FromHours(1);
    private static readonly TimeSpan WakeValidFor = TimeSpan.FromHours(1);
    /// <summary>Extra time the relay waits beyond that, for an agent that HAS picked the request
    /// up to finish connecting.</summary>
    private static readonly TimeSpan ConnectGrace = TimeSpan.FromMinutes(2);

    private static readonly JsonSerializerOptions JsonRead = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonNodeOptions NodeOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions JsonWrite = new() { WriteIndented = true };

    private readonly ConsoleOptions _opt;
    private readonly GitRepo _git;
    private readonly RelayHub _relay;
    private readonly RelayKeyStore _keys;
    private readonly ControlSigning _signing;
    private readonly RelayAdvert? _advert;   // the console's own LAN name; null = nothing to say
    private readonly TunnelService? _tunnel; // when it is up, THIS is what PCs are told to dial (it works from any network)
    private RelayAdvert? CurrentAdvert => _tunnel?.Advert ?? _advert;
    private readonly ILogger<ControlRepo> _log;

    /// <summary>The files agents refuse unless signed (agent.json too, but CI signs that one).</summary>
    private static readonly string[] SignedFiles = { ManifestPath, "commands.json" };

    public ControlRepo(ConsoleOptions opt, RelayHub relay, RelayKeyStore keys, ControlSigning signing, ILogger<ControlRepo> log, RelayAdvert? advert = null, TunnelService? tunnel = null)
    {
        _signing = signing;
        _advert = advert;
        _tunnel = tunnel;
        _opt = opt;
        _git = new GitRepo(opt.ControlRepoPath);
        _relay = relay;
        _keys = keys;
        _log = log;
    }

    public bool RepoIsValid() => _git.IsValid();
    public string RepoPath => _opt.ControlRepoPath;

    private string MainRef => $"{_opt.Remote}/{_opt.MainBranch}";
    private string FleetRef => $"{_opt.Remote}/{_opt.FleetStateBranch}";

    // ---- one writer at a time, and one retry when GitHub moved ----------------------------

    /// <summary>Every console write is fetch, edit files in the clone, commit, push. Two at once
    /// (two quick clicks, or Save then Run now) would edit the same working tree and fight over
    /// git's index lock, so they take turns.</summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>Run one write under the gate. If the push is rejected because GitHub moved on
    /// while we worked (CI publishes agent.json here on every agent build), the clone has already
    /// been put back, so the whole operation is simply run again from a fresh fetch — it is
    /// written to be re-runnable, since each edit is re-derived from what main contains now. A
    /// second rejection is reported instead of looping.</summary>
    private async Task<T> MutateAsync<T>(Func<Task<T>> body, Func<string, T> fail, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            try { return await body(); }
            catch (PushRejectedException first)
            {
                _log.LogInformation("push rejected ({Reason}); retrying once from a fresh fetch", first.Message.Split('\n')[0]);
                try { return await body(); }
                catch (PushRejectedException second)
                {
                    return fail($"GitHub didn't accept the push (twice): {second.Message.Split('\n')[0]}. Nothing was changed on your computer or on GitHub; try again in a moment.");
                }
            }
        }
        finally { _writeGate.Release(); }
    }

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

        // --- the agent build the fleet is supposed to be on (agent.json, written by CI) ---
        resp.ExpectedAgentSha256 = ReadExpectedAgentSha();

        // --- heartbeats (from state/*.json on the fleet-state branch) ---
        var heartbeats = ReadHeartbeats();
        if (heartbeats.Count == 0)
            resp.Warnings.Add($"No machines have reported yet (branch '{_opt.FleetStateBranch}' is empty or missing).");

        resp.Machines = heartbeats
            .Select(hb => ToMachineView(hb, labels, ReadLatestScreenshotMeta(hb.MachineId), CheckRelayKey(hb, resp.Warnings)))
            .OrderByDescending(m => m.Online)
            .ThenBy(m => m.Label ?? m.Hostname, StringComparer.OrdinalIgnoreCase)
            .ToList();

        resp.Programs = programNodes.Select(ToProgramView).ToList();
        resp.Signing = ReadSigningStatus(resp.Warnings);
        return resp;
    }

    /// <summary>Apply targeting/activation + label edits to the repo and push. Returns the outcome.</summary>
    public Task<SaveResult> SaveAsync(SaveRequest req, CancellationToken ct)
        => MutateAsync(() => SaveCoreAsync(req, ct), m => new SaveResult { Ok = false, Message = m }, ct);

    private async Task<SaveResult> SaveCoreAsync(SaveRequest req, CancellationToken ct)
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
            var sha = await CommitSignedAsync(
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
    public Task<SaveResult> AddProgramAsync(AddRequest req, CancellationToken ct)
        => MutateAsync(() => AddProgramCoreAsync(req, ct), m => new SaveResult { Ok = false, Message = m }, ct);

    private async Task<SaveResult> AddProgramCoreAsync(AddRequest req, CancellationToken ct)
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
            var sha = await CommitSignedAsync(
                $"console: add program {id} ({DateTimeOffset.UtcNow:u})", toCommit, ct);
            return new SaveResult { Ok = true, Message = $"Added {id}.", Commit = sha };
        }
        catch (GitException ex) { return Fail($"Commit/push failed: {ex.Message}"); }
    }

    /// <summary>Trigger a one-time interactive "run now" by rotating the program's runRequest token, then push.</summary>
    public Task<SaveResult> RunNowAsync(string programId, CancellationToken ct)
        => MutateAsync(() => RunNowCoreAsync(programId, ct), m => new SaveResult { Ok = false, Message = m }, ct);

    private async Task<SaveResult> RunNowCoreAsync(string programId, CancellationToken ct)
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
            var sha = await CommitSignedAsync(
                $"console: run-now {programId} ({DateTimeOffset.UtcNow:u})", new[] { ManifestPath }, ct);
            return new SaveResult { Ok = true, Message = $"Run-now requested — it runs on each targeted machine's next sync.", Commit = sha };
        }
        catch (GitException ex) { return Fail($"Commit/push failed: {ex.Message}"); }
    }

    /// <summary>Remove a program from the manifest entirely (and its repo file), then push.
    /// Machines that have it uninstall it on their next sync; machines that never had it are unaffected.</summary>
    public Task<SaveResult> DeleteProgramAsync(string programId, CancellationToken ct)
        => MutateAsync(() => DeleteProgramCoreAsync(programId, ct), m => new SaveResult { Ok = false, Message = m }, ct);

    private async Task<SaveResult> DeleteProgramCoreAsync(string programId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(programId)) return Fail("Program id is required.");
        var err = await PrepareCleanMainAsync(ct);
        if (err is not null) return Fail(err);

        var repoRoot = _opt.ControlRepoPath;
        var manifestFull = Path.Combine(repoRoot, ManifestPath);
        if (!File.Exists(manifestFull)) return Fail($"{ManifestPath} not found.");
        var root = JsonNode.Parse(File.ReadAllText(manifestFull), NodeOpts) as JsonObject
                   ?? throw new InvalidOperationException("manifest.json is not a JSON object.");
        if (root["programs"] is not JsonArray progs) return Fail("manifest.json has no 'programs' array.");

        // Find and remove the program object.
        var idx = -1;
        JsonObject? match = null;
        for (var i = 0; i < progs.Count; i++)
        {
            if (progs[i] is JsonObject o &&
                string.Equals(o["id"]?.GetValue<string>(), programId, StringComparison.OrdinalIgnoreCase))
            { match = o; idx = i; break; }
        }
        if (match is null || idx < 0) return Fail($"Program '{programId}' not found.");

        var relPath = match["path"]?.GetValue<string>();   // its file in the repo, if any
        progs.RemoveAt(idx);
        root["lastUpdated"] = DateTimeOffset.UtcNow.ToString("O");
        File.WriteAllText(manifestFull, root.ToJsonString(JsonWrite));

        var toCommit = new List<string> { ManifestPath };
        // Also remove the program's file from the repo (only a safe, repo-relative path).
        if (!string.IsNullOrWhiteSpace(relPath) && !relPath.Contains("..") && !Path.IsPathRooted(relPath))
        {
            var full = Path.Combine(repoRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full))
            {
                try { File.Delete(full); toCommit.Add(relPath); }
                catch (Exception ex) { _log.LogWarning(ex, "Could not delete repo file {Path}", relPath); }
            }
        }

        if (_git.IsClean()) return new SaveResult { Ok = true, Message = "Nothing to delete.", Commit = null };
        try
        {
            var sha = await CommitSignedAsync(
                $"console: delete program {programId} ({DateTimeOffset.UtcNow:u})", toCommit, ct);
            return new SaveResult { Ok = true, Message = "Deleted — machines uninstall it on their next sync.", Commit = sha };
        }
        catch (GitException ex) { return Fail($"Commit/push failed: {ex.Message}"); }
    }

    /// <summary>Send an admin command (shutdown/restart) to a machine via commands.json, then push.</summary>
    public Task<SaveResult> SendCommandAsync(string machineId, string action, CancellationToken ct)
        => MutateAsync(() => SendCommandCoreAsync(machineId, action, ct), m => new SaveResult { Ok = false, Message = m }, ct);

    private async Task<SaveResult> SendCommandCoreAsync(string machineId, string action, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(machineId)) return Fail("Machine id is required.");
        action = (action ?? string.Empty).Trim().ToLowerInvariant();
        if (action is not ("shutdown" or "restart" or "uninstall")) return Fail("Action must be 'shutdown', 'restart' or 'uninstall'.");

        var err = await PrepareCleanMainAsync(ct);
        if (err is not null) return Fail(err);

        const string cmdPath = "commands.json";
        var full = Path.Combine(_opt.ControlRepoPath, cmdPath);
        var root = File.Exists(full)
            ? (JsonNode.Parse(File.ReadAllText(full), NodeOpts) as JsonObject ?? new JsonObject())
            : new JsonObject();
        if (root["commands"] is not JsonObject cmds) { cmds = new JsonObject(); root["commands"] = cmds; }
        cmds[machineId] = new JsonObject
        {
            ["action"] = action,
            ["id"] = Guid.NewGuid().ToString("N"),   // fresh nonce = run once on the target
            ["requestedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            // Uninstall is short-lived on purpose: a machine reinstalled later, which has forgotten which
            // commands it already ran, must not find an old one waiting and remove its fresh agent.
            ["expiresUtc"] = DateTimeOffset.UtcNow.Add(action == "uninstall" ? UninstallValidFor : CommandValidFor).ToString("O")   // stale after this
        };
        File.WriteAllText(full, root.ToJsonString(JsonWrite));

        try
        {
            var sha = await CommitSignedAsync(
                $"console: {action} {machineId} ({DateTimeOffset.UtcNow:u})", new[] { cmdPath }, ct);
            return new SaveResult
            {
                Ok = true,
                Commit = sha,
                Message = action == "uninstall"
                    ? "Uninstall sent: the agent removes itself on the machine's next sync (the PC has to be on within the hour), then it leaves this list."
                    : $"{action} sent — runs on the machine's next sync (~1 cycle, 15s delay)."
            };
        }
        catch (GitException ex) { return Fail($"Commit/push failed: {ex.Message}"); }
    }

    /// <summary>Take a machine off the console's list: delete its heartbeat (and screenshots) from the
    /// fleet-state branch, and forget its remote-control key. This does NOT touch the PC itself: an
    /// agent that is still running reports again and reappears; use the uninstall command to remove it.
    /// For machines that are gone for good (a wiped VM, a duplicate after a reinstall).</summary>
    public async Task<SaveResult> ForgetMachineAsync(string machineId, CancellationToken ct)
    {
        // The id becomes part of a path in the repo, so it has to look like the ids agents generate.
        if (string.IsNullOrWhiteSpace(machineId) || !System.Text.RegularExpressions.Regex.IsMatch(machineId, @"^[A-Za-z0-9._\-]{1,128}$") || machineId.Contains(".."))
            return Fail("That isn't a machine id.");

        await _writeGate.WaitAsync(ct);
        try
        {
            await _git.FetchAsync(_opt.Remote, ct);   // read the machine's name from what GitHub holds now
            var host = ReadHeartbeats().FirstOrDefault(h => string.Equals(h.MachineId, machineId, StringComparison.OrdinalIgnoreCase))?.Hostname;
            bool removed;
            try
            {
                removed = await _git.RemovePathsOnRemoteBranchAsync(_opt.Remote, _opt.FleetStateBranch,
                    new[] { $"{StateDir}/{machineId}.json", $"screenshots/{machineId}" },
                    $"console: removed machine {host ?? machineId} ({machineId})", ct);
            }
            catch (GitException ex) { return Fail($"Couldn't remove it from GitHub: {ex.Message}"); }

            try { _keys.Unpin(machineId); }
            catch (InvalidOperationException) { /* an unreadable pin file is reported elsewhere; it doesn't block removal */ }
            await _git.FetchAsync(_opt.Remote, ct);   // so the very next page load already lacks it
            return new SaveResult
            {
                Ok = true,
                Message = removed
                    ? "Removed from the list. If its agent is still running it will report again and come back; use Uninstall to remove the agent."
                    : "It was already gone from the list."
            };
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>Queue Wake-on-LAN requests for the given machines (by their reported MAC), then push.</summary>
    public Task<SaveResult> SendWakeAsync(List<string> machineIds, CancellationToken ct)
        => MutateAsync(() => SendWakeCoreAsync(machineIds, ct), m => new SaveResult { Ok = false, Message = m }, ct);

    private async Task<SaveResult> SendWakeCoreAsync(List<string> machineIds, CancellationToken ct)
    {
        if (machineIds is null || machineIds.Count == 0) return Fail("No machines selected.");
        var err = await PrepareCleanMainAsync(ct);
        if (err is not null) return Fail(err);

        // machine id -> primary MAC, from the heartbeats.
        var macById = ReadHeartbeats()
            .Where(h => h.MacAddresses.Count > 0)
            .GroupBy(h => h.MachineId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().MacAddresses[0], StringComparer.OrdinalIgnoreCase);

        const string cmdPath = "commands.json";
        var full = Path.Combine(_opt.ControlRepoPath, cmdPath);
        var root = File.Exists(full)
            ? (JsonNode.Parse(File.ReadAllText(full), NodeOpts) as JsonObject ?? new JsonObject())
            : new JsonObject();

        var wake = new JsonArray();
        var missing = 0;
        foreach (var id in machineIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!macById.TryGetValue(id, out var mac)) { missing++; continue; }
            wake.Add(new JsonObject
            {
                ["mac"] = mac,
                ["machineId"] = id,
                ["id"] = Guid.NewGuid().ToString("N"),
                ["requestedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                ["expiresUtc"] = DateTimeOffset.UtcNow.Add(WakeValidFor).ToString("O")   // a new waker won't replay it
            });
        }
        if (wake.Count == 0)
            return Fail("None of the selected machines have a known MAC yet (they must have reported a heartbeat at least once).");

        root["wake"] = wake;   // replace with the current batch (keeps commands.json bounded)
        File.WriteAllText(full, root.ToJsonString(JsonWrite));

        try
        {
            var sha = await CommitSignedAsync(
                $"console: wake {wake.Count} machine(s) ({DateTimeOffset.UtcNow:u})", new[] { cmdPath }, ct);
            var msg = $"Wake queued for {wake.Count} machine(s) — the waker sends the packets on its next sync.";
            if (missing > 0) msg += $" ({missing} skipped: no MAC reported yet.)";
            return new SaveResult { Ok = true, Message = msg, Commit = sha };
        }
        catch (GitException ex) { return Fail($"Commit/push failed: {ex.Message}"); }
    }

    /// <summary>Request a screenshot capture from a machine via commands.json, then push. The
    /// actual capture happens on the machine's next sync, in its logged-on user's session
    /// (a Windows service has no desktop of its own) — so it needs someone signed in there.</summary>
    public Task<SaveResult> SendScreenshotAsync(string machineId, CancellationToken ct)
        => MutateAsync(() => SendScreenshotCoreAsync(machineId, ct), m => new SaveResult { Ok = false, Message = m }, ct);

    private async Task<SaveResult> SendScreenshotCoreAsync(string machineId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(machineId)) return Fail("Machine id is required.");
        var err = await PrepareCleanMainAsync(ct);
        if (err is not null) return Fail(err);

        const string cmdPath = "commands.json";
        var full = Path.Combine(_opt.ControlRepoPath, cmdPath);
        var root = File.Exists(full)
            ? (JsonNode.Parse(File.ReadAllText(full), NodeOpts) as JsonObject ?? new JsonObject())
            : new JsonObject();
        if (root["screenshots"] is not JsonObject shots) { shots = new JsonObject(); root["screenshots"] = shots; }
        shots[machineId] = new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString("N"),   // fresh nonce = capture once on the target
            ["requestedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["expiresUtc"] = DateTimeOffset.UtcNow.Add(ScreenshotValidFor).ToString("O")   // stale after this
        };
        File.WriteAllText(full, root.ToJsonString(JsonWrite));

        try
        {
            var sha = await CommitSignedAsync(
                $"console: screenshot {machineId} ({DateTimeOffset.UtcNow:u})", new[] { cmdPath }, ct);
            return new SaveResult { Ok = true, Message = "Screenshot requested — captured on the machine's next sync (needs a logged-on user).", Commit = sha };
        }
        catch (GitException ex) { return Fail($"Commit/push failed: {ex.Message}"); }
    }

    /// <summary>Fetch, then return a machine's latest captured screenshot as JPEG bytes, or null if none.</summary>
    public async Task<byte[]?> GetLatestScreenshotAsync(string machineId, CancellationToken ct)
    {
        await _git.FetchAsync(_opt.Remote, ct);
        var meta = ReadLatestScreenshotMeta(machineId);
        if (meta is null || string.IsNullOrWhiteSpace(meta.Path)) return null;
        return _git.ReadFileBytesFromRef(FleetRef, meta.Path);
    }

    /// <summary>Queue a live remote-control session on a machine via commands.json, then push,
    /// and register the session's single-use token with the relay so the eventual agent/viewer
    /// connections are recognized. The actual session starts on the machine's next sync, in its
    /// logged-on user's session — same constraint as screenshots.</summary>
    public Task<RemoteSessionResult> StartRemoteSessionAsync(string machineId, CancellationToken ct)
        => MutateAsync(() => StartRemoteSessionCoreAsync(machineId, ct), m => new RemoteSessionResult { Ok = false, Message = m }, ct);

    private async Task<RemoteSessionResult> StartRemoteSessionCoreAsync(string machineId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(machineId)) return new RemoteSessionResult { Ok = false, Message = "Machine id is required." };

        // Don't stack sessions. Queuing a second one gives that machine two banners and two
        // capture loops streaming the same desktop — which is what happens if the first click
        // feels slow and you click again. Hand back the existing session instead.
        var existing = _relay.FindSessionForMachine(machineId);
        if (existing is not null)
            return new RemoteSessionResult
            {
                Ok = true,
                AlreadyRunning = true,
                SessionId = existing,
                Message = "A session is already open for this machine — reopening its viewer."
            };

        var err = await PrepareCleanMainAsync(ct);
        if (err is not null) return new RemoteSessionResult { Ok = false, Message = err };

        // Only a machine whose signing key we trust can take a session: the session id below is
        // readable by every machine, so the key is what stops one of them answering for another.
        var hb = ReadHeartbeats().FirstOrDefault(h => string.Equals(h.MachineId, machineId, StringComparison.OrdinalIgnoreCase));
        if (hb is null) return new RemoteSessionResult { Ok = false, Message = "That machine hasn't reported a heartbeat yet." };
        var keyStatus = CheckRelayKey(hb, warnings: null);
        if (keyStatus != "trusted")
            return new RemoteSessionResult { Ok = false, Message = RelayKeyRefusal(keyStatus) };
        var agentKey = hb.RelayPublicKey!;

        var sessionId = Guid.NewGuid().ToString("N");
        var requestedUtc = DateTimeOffset.UtcNow;
        // How long the agent may take to NOTICE the request (it only looks once per sync). Past
        // this it discards the request rather than ambushing whoever is at that machine with a
        // session they asked for half an hour ago. A machine whose SyncIntervalMinutes is close
        // to this will miss requests — see docs/TROUBLESHOOTING.md.
        var expiresUtc = requestedUtc.Add(PickupWindow);

        const string cmdPath = "commands.json";
        var full = Path.Combine(_opt.ControlRepoPath, cmdPath);
        var root = File.Exists(full)
            ? (JsonNode.Parse(File.ReadAllText(full), NodeOpts) as JsonObject ?? new JsonObject())
            : new JsonObject();
        if (root["remoteSessions"] is not JsonObject sessions) { sessions = new JsonObject(); root["remoteSessions"] = sessions; }
        sessions[machineId] = new JsonObject
        {
            ["id"] = sessionId,
            ["requestedUtc"] = requestedUtc.ToString("O"),
            ["expiresUtc"] = expiresUtc.ToString("O")
        };
        File.WriteAllText(full, root.ToJsonString(JsonWrite));

        // Tell the machine where to dial BEFORE the request becomes visible, so an agent that sees
        // the request can always find the address. It goes on a throwaway branch, never main.
        var hinted = false;
        try
        {
            hinted = await PublishRelayHintAsync(machineId, sessionId, ct);
        }
        catch (GitException ex)
        {
            await _git.RollbackAsync(await _git.HeadAsync(ct), new[] { cmdPath });
            return new RemoteSessionResult { Ok = false, Message = $"Couldn't publish the session address: {ex.Message}" };
        }

        try
        {
            var sha = await CommitSignedAsync(
                $"console: remote-session {machineId} ({DateTimeOffset.UtcNow:u})", new[] { cmdPath }, ct);
            // Derive this from expiresUtc rather than hardcoding it: the viewer shows this
            // deadline as a countdown, so a window longer than the agent's own pickup window
            // would leave it counting down long after the agent discarded the request as stale.
            // The grace covers the agent connecting AFTER it picks the request up (its one-time
            // task fires ~15s later, then has to reach the relay).
            _relay.RegisterPending(sessionId, machineId, agentKey, validFor: expiresUtc - DateTimeOffset.UtcNow + ConnectGrace);
            return new RemoteSessionResult
            {
                Ok = true,
                SessionId = sessionId,
                Message = "Session requested — connects on the machine's next sync (needs a logged-on user).",
                Commit = sha
            };
        }
        catch (GitException ex)
        {
            if (hinted) await TryRemoveRelayHintAsync(machineId, sessionId);
            return new RemoteSessionResult { Ok = false, Message = $"Commit/push failed: {ex.Message}" };
        }
        catch (PushRejectedException)
        {
            // MutateAsync is about to run this whole operation again with a new session id.
            if (hinted) await TryRemoveRelayHintAsync(machineId, sessionId);
            throw;
        }
    }

    // ---- the throwaway relay hint ---------------------------------------------------------
    // Where a PC should dial for a live session is published only for as long as the session
    // can still start, on its own branch with a single parentless commit that is force-pushed
    // and finally deleted — so it never enters the control repo's normal history, and nothing is
    // left once the session is over. It is signed like the other control files; agents that
    // enforce signing are the only ones that honour it (see RelayHint in the service).

    private const string HintBranch = "relay-hint";
    private const string HintPath = "relay-hint.json";
    private string HintRef => $"{_opt.Remote}/{HintBranch}";

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _hintIds = new();   // sessions we published
    private volatile bool _hintMayBeLeftOver;   // true after a console restart: the branch might hold sessions we forgot

    /// <summary>Add this machine's session to the hint. Returns false when there is nothing to publish
    /// (no address to advertise, or no key to sign with): the PC then uses its installed address.
    /// Must be called with the write gate held.</summary>
    private async Task<bool> PublishRelayHintAsync(string machineId, string sessionId, CancellationToken ct)
    {
        var advert = CurrentAdvert;   // read once: the tunnel can drop between two reads
        if (advert is null || !_signing.HasKey) return false;
        await RewriteRelayHintAsync(sessions =>
        {
            sessions[machineId] = new JsonObject
            {
                ["id"] = sessionId,
                ["relayUrl"] = advert.Url,
                ["relayCertThumbprint"] = advert.Thumbprint
            };
        }, ct, alsoKeep: sessionId);   // the relay only learns of this session once its request is on GitHub
        _hintIds[sessionId] = 0;
        return true;
    }

    /// <summary>Read the hint as it is on GitHub, let <paramref name="edit"/> change its sessions,
    /// drop every session the relay no longer knows, then publish the result — or delete the branch
    /// when nothing is left. Must be called with the write gate held, after a fetch.</summary>
    private async Task RewriteRelayHintAsync(Action<JsonObject> edit, CancellationToken ct, string? alsoKeep = null)
    {
        var current = _git.ReadFileBytesFromRef(HintRef, HintPath);
        var previousSig = _git.ReadFileBytesFromRef(HintRef, HintPath + ".sig");
        JsonObject sessions = new();
        try
        {
            if (current is not null && JsonNode.Parse(current, NodeOpts) is JsonObject root && root["sessions"] is JsonObject s)
                sessions = s;
        }
        catch (JsonException) { /* an unreadable hint is replaced, not repaired */ }

        edit(sessions);
        foreach (var machine in sessions.Select(kv => kv.Key).ToList())
        {
            var id = (sessions[machine] as JsonObject)?["id"]?.GetValue<string>();
            if (id is null || (id != alsoKeep && !_relay.IsValid(id))) sessions.Remove(machine);   // over, expired, or never registered
        }

        if (sessions.Count == 0)
        {
            await _git.DeleteRemoteBranchAsync(_opt.Remote, HintBranch, ct);
            _hintIds.Clear();
            _hintMayBeLeftOver = false;
            return;
        }

        var content = Encoding.UTF8.GetBytes(new JsonObject { ["sessions"] = sessions }.ToJsonString(JsonWrite) + "\n");
        var sig = _signing.Sign(HintPath, content, previousSig);
        await _git.PublishOrphanBranchAsync(_opt.Remote, HintBranch,
            new Dictionary<string, byte[]> { [HintPath] = content, [HintPath + ".sig"] = sig },
            "console: relay hint", ct);
        foreach (var id in _hintIds.Keys.ToList())
            if (!sessions.Any(kv => (kv.Value as JsonObject)?["id"]?.GetValue<string>() == id)) _hintIds.TryRemove(id, out _);
    }

    /// <summary>Best effort: take one session out of the hint after its request couldn't be sent.
    /// (The janitor would catch it anyway; this just doesn't wait for it.)</summary>
    private async Task TryRemoveRelayHintAsync(string machineId, string sessionId)
    {
        try
        {
            await _git.FetchAsync(_opt.Remote, CancellationToken.None);
            await RewriteRelayHintAsync(sessions =>
            {
                if ((sessions[machineId] as JsonObject)?["id"]?.GetValue<string>() == sessionId) sessions.Remove(machineId);
            }, CancellationToken.None);
        }
        catch (Exception ex) { _log.LogWarning(ex, "couldn't take a failed session out of the relay hint; the janitor will"); }
    }

    /// <summary>Called when the console starts: a previous run may have died with sessions still
    /// published, and it can no longer vouch for any of them.</summary>
    public async Task NoteStartupAsync(CancellationToken ct)
    {
        try { _hintMayBeLeftOver = await _git.RemoteBranchExistsAsync(_opt.Remote, HintBranch, ct); }
        catch (Exception ex) { _log.LogWarning(ex, "couldn't check GitHub for a leftover relay hint"); }
    }

    /// <summary>Remove from the hint every session that is over (or never started). Cheap when there is
    /// nothing to do: it only touches the network when a session it published is no longer live.</summary>
    public async Task SweepRelayHintAsync(CancellationToken ct)
    {
        if (!_hintMayBeLeftOver && !_hintIds.Keys.Any(id => !_relay.IsValid(id))) return;
        await _writeGate.WaitAsync(ct);
        try
        {
            await _git.FetchAsync(_opt.Remote, ct);
            await RewriteRelayHintAsync(_ => { }, ct);
        }
        catch (Exception ex) when (ex is GitException or InvalidOperationException)
        {
            _log.LogWarning(ex, "couldn't clean up the relay hint; will try again");
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>Why a machine can't take a remote session, in words that name the fix.</summary>
    private string RelayKeyRefusal(string status) => status switch
    {
        "changed" => "This machine's remote-control key has changed since the console first saw it. That's expected after a " +
                     "reinstall, but it's also what an impersonating machine looks like. If you reinstalled it, press \"Trust new key\".",
        "error" => $"The console couldn't read its remote-control key pins ({_keys.FilePath}). Fix or delete that file.",
        _ => "This machine hasn't reported a remote-control key yet. Its agent is older than this console; it picks up " +
             "the new build on a coming sync, then Remote works."
    };

    /// <summary>Accept a machine's current (changed) key as genuine — the operator's call, after a
    /// reinstall. Reads the key fresh from the heartbeat so it's the one the machine reports now.</summary>
    public async Task<RemoteSessionResult> TrustRelayKeyAsync(string machineId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(machineId)) return new RemoteSessionResult { Ok = false, Message = "Machine id is required." };
        await _git.FetchAsync(_opt.Remote, ct);
        var hb = ReadHeartbeats().FirstOrDefault(h => string.Equals(h.MachineId, machineId, StringComparison.OrdinalIgnoreCase));
        if (hb is null || string.IsNullOrWhiteSpace(hb.RelayPublicKey))
            return new RemoteSessionResult { Ok = false, Message = "That machine isn't reporting a key to trust." };
        try { _keys.Trust(hb.MachineId, hb.RelayPublicKey); }
        catch (InvalidOperationException ex) { return new RemoteSessionResult { Ok = false, Message = ex.Message }; }
        return new RemoteSessionResult { Ok = true, Message = "New key trusted. Remote control works for this machine again." };
    }

    // ---- edit helpers ------------------------------------------------------------------

    /// <summary>Fetch, verify the clone is clean, and fast-forward main. Returns an error message or null.
    /// With a signing key present it also refuses to build on top of a control file that isn't
    /// validly signed: the console signs the WHOLE file it saves, so editing one that someone
    /// else changed would sign their change too — exactly what signing exists to prevent.</summary>
    private async Task<string?> PrepareCleanMainAsync(CancellationToken ct, bool requireSigned = true)
    {
        await _git.FetchAsync(_opt.Remote, ct);
        if (!_git.IsClean())
            return "The control-repo clone has uncommitted changes. Commit or discard them, then retry.";
        try { await _git.SyncBranchToRemoteAsync(_opt.Remote, _opt.MainBranch, ct); }
        catch (GitException ex)
        {
            return $"Your local copy of '{_opt.MainBranch}' and GitHub have each moved on (a commit made here that was never pushed, " +
                   $"plus newer ones on GitHub — the auto-publish adds one for every agent build). Fix it once in a terminal in " +
                   $"{_opt.ControlRepoPath}: git pull --rebase {_opt.Remote} {_opt.MainBranch} && git push {_opt.Remote} {_opt.MainBranch}   " +
                   $"(details: {ex.Message.Split('\n')[0]})";
        }
        if (requireSigned && _signing.HasKey && ReadSigningStatus(null).Unsigned.FirstOrDefault() is { } u)
            return $"{u.Path} has changes that aren't signed ({(u.LastChange is null ? "" : "last changed by " + u.LastChange + "; ")}" +
                   "made outside this console, or by another key). Check that change on GitHub; if it's yours, press Sign, then retry.";
        return null;
    }

    /// <summary>Which signed control files on main aren't validly signed by our key. Adds page
    /// warnings when given a list.</summary>
    private SigningView ReadSigningStatus(List<string>? warnings)
    {
        var view = new SigningView { KeyPresent = _signing.HasKey };
        if (!_signing.HasKey)
        {
            warnings?.Add("Control files aren't being signed: this computer has no signing key, so a GitHub token copied from any " +
                "machine could change what the whole fleet runs. Set it up once: docs/SETUP.md, \"Sign the control files\".");
            return view;
        }
        foreach (var path in SignedFiles)
        {
            var content = _git.ReadFileBytesFromRef(MainRef, path);
            if (content is null) continue;   // e.g. no commands.json yet: nothing to sign
            var state = _signing.Check(path, content, _git.ReadFileBytesFromRef(MainRef, path + ".sig"));
            if (state == SignatureState.Valid) continue;
            view.Unsigned.Add(new UnsignedFile
            {
                Path = path,
                State = state == SignatureState.Missing ? "missing" : "invalid",
                LastChange = _git.LastChange(MainRef, path)
            });
        }
        if (warnings is not null && view.Unsigned.Count > 0)
            warnings.Add($"Not signed: {string.Join(", ", view.Unsigned.Select(u => u.Path + (u.LastChange is null ? "" : $" (last changed by {u.LastChange})")))}. " +
                "Machines ignore these until they're signed. If the change is yours, press Sign; if not, someone else is editing your control repo.");
        return view;
    }

    /// <summary>Stage, sign (when we have a key), commit and push. Signs each control file in
    /// <paramref name="paths"/> whose content or signature differs from main, so the commit
    /// carries the .sig the agents will check.</summary>
    private async Task<string> CommitSignedAsync(string message, IEnumerable<string> paths, CancellationToken ct)
    {
        var all = paths.ToList();
        var start = await _git.HeadAsync(ct);
        try
        {
            return await SignAndCommitAsync(message, all, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // CommitAndPushAsync undoes its own failures; this covers the signing step before it,
            // which may already have written .sig files into the tree.
            await _git.RollbackAsync(start, all.Concat(SignedFiles.Select(f => f + ".sig")));
            throw;
        }
    }

    private async Task<string> SignAndCommitAsync(string message, List<string> all, CancellationToken ct)
    {
        if (_signing.HasKey)
        {
            foreach (var path in SignedFiles.Where(f => all.Contains(f)))
            {
                var full = Path.Combine(_opt.ControlRepoPath, path);
                if (!File.Exists(full)) continue;
                var content = File.ReadAllBytes(full);
                var sigOnMain = _git.ReadFileBytesFromRef(MainRef, path + ".sig");
                var onMain = _git.ReadFileBytesFromRef(MainRef, path);
                if (onMain is not null && onMain.AsSpan().SequenceEqual(content)
                    && _signing.Check(path, content, sigOnMain) == SignatureState.Valid)
                    continue;   // unchanged and already signed: leave its signature alone
                File.WriteAllBytes(full + ".sig", _signing.Sign(path, content, sigOnMain));
                all.Add(path + ".sig");
            }
        }
        return await _git.CommitAndPushAsync(_opt.Remote, _opt.MainBranch, message, all, ct);
    }

    /// <summary>The operator vouches for the control files exactly as they are on main now
    /// (after a hand edit, or the first time signing is set up), and signs them.</summary>
    public Task<SaveResult> SignNowAsync(CancellationToken ct)
        => MutateAsync(() => SignNowCoreAsync(ct), m => new SaveResult { Ok = false, Message = m }, ct);

    private async Task<SaveResult> SignNowCoreAsync(CancellationToken ct)
    {
        if (!_signing.HasKey) return new SaveResult { Ok = false, Message = "This computer has no signing key — see docs/SETUP.md, \"Sign the control files\"." };
        var err = await PrepareCleanMainAsync(ct, requireSigned: false);
        if (err is not null) return new SaveResult { Ok = false, Message = err };

        var toSign = new List<string>();
        foreach (var path in SignedFiles)
        {
            var full = Path.Combine(_opt.ControlRepoPath, path);
            if (!File.Exists(full)) continue;
            var content = File.ReadAllBytes(full);
            if (_signing.Check(path, content, File.Exists(full + ".sig") ? File.ReadAllBytes(full + ".sig") : null) == SignatureState.Valid) continue;
            File.WriteAllBytes(full + ".sig", _signing.Sign(path, content, File.Exists(full + ".sig") ? File.ReadAllBytes(full + ".sig") : null));
            toSign.Add(path + ".sig");
        }
        if (toSign.Count == 0) return new SaveResult { Ok = true, Message = "Everything is already signed." };
        try
        {
            var sha = await _git.CommitAndPushAsync(_opt.Remote, _opt.MainBranch,
                $"console: sign {string.Join(", ", toSign.Select(p => p[..^4]))} ({DateTimeOffset.UtcNow:u})", toSign, ct);
            return new SaveResult { Ok = true, Message = $"Signed {string.Join(", ", toSign.Select(p => p[..^4]))}.", Commit = sha };
        }
        catch (GitException ex) { return new SaveResult { Ok = false, Message = $"Commit/push failed: {ex.Message}" }; }
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

    /// <summary>Pin-or-compare this machine's remote-control key (see RelayKeyStore). A pin file
    /// the console can't read is reported once as a page warning rather than breaking the page.</summary>
    private string CheckRelayKey(HeartbeatFile hb, List<string>? warnings)
    {
        try
        {
            return _keys.Check(hb.MachineId, hb.RelayPublicKey) switch
            {
                RelayKeyStatus.Trusted => "trusted",
                RelayKeyStatus.Changed => "changed",
                _ => "missing"
            };
        }
        catch (InvalidOperationException ex)
        {
            if (warnings is not null && !warnings.Contains(ex.Message)) warnings.Add(ex.Message);
            return "error";
        }
    }

    private static MachineView ToMachineView(HeartbeatFile hb, Dictionary<string, string> labels, ScreenshotMeta? shot, string relayKey)
    {
        labels.TryGetValue(hb.MachineId, out var label);
        return new MachineView
        {
            MachineId = hb.MachineId,
            Hostname = hb.Hostname,
            Label = label,
            Os = hb.Os,
            AgentVersion = hb.AgentVersion,
            AgentSha256 = hb.AgentSha256,
            LastSeenUtc = hb.LastSeenUtc,
            Online = IsOnline(hb),
            LastSyncSuccess = hb.LastSyncSuccess,
            ManifestVersion = hb.ManifestVersion,
            LastError = hb.LastError,
            AppliedProgramIds = hb.AppliedProgramIds,
            Mac = hb.MacAddresses.FirstOrDefault(),
            LastScreenshotUtc = shot?.CapturedUtc,
            RelayKey = relayKey
        };
    }

    /// <summary>The SHA-256 CI published in agent.json, or null if self-update isn't set up.
    /// Read leniently — a control repo without agent.json is a perfectly normal state, not an
    /// error, and must not stop the rest of the page loading.</summary>
    private string? ReadExpectedAgentSha()
    {
        var text = _git.ReadFileFromRef(MainRef, "agent.json");
        if (text is null) return null;
        try
        {
            var node = JsonNode.Parse(text, NodeOpts) as JsonObject;
            var sha = node?["sha256"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(sha) ? null : sha.Trim().ToLowerInvariant();
        }
        catch { return null; }
    }

    /// <summary>Read a machine's latest screenshot pointer from the fleet-state branch, if any.</summary>
    private ScreenshotMeta? ReadLatestScreenshotMeta(string machineId)
    {
        var text = _git.ReadFileFromRef(FleetRef, $"screenshots/{machineId}/latest.json");
        if (text is null) return null;
        try { return JsonSerializer.Deserialize<ScreenshotMeta>(text, JsonRead); }
        catch { return null; }   // malformed pointer -> treat as "no screenshot yet"
    }

    /// <summary>Heartbeat cadence assumed for agents that don't report it (their old built-in default).</summary>
    private const int LegacyHeartbeatMaxIntervalMinutes = 360;

    /// <summary>
    /// A machine is "online" if its last heartbeat is no older than it could legitimately be.
    /// An agent with nothing new to say pushes only every heartbeatMaxIntervalMinutes, so
    /// judging by the sync interval alone showed every idle, healthy machine as offline.
    /// </summary>
    private static bool IsOnline(HeartbeatFile hb)
    {
        if (!DateTimeOffset.TryParse(hb.LastSeenUtc, out var seen)) return false;
        var interval = Math.Max(1, hb.SyncIntervalMinutes);
        var refresh = hb.HeartbeatMaxIntervalMinutes > 0 ? hb.HeartbeatMaxIntervalMinutes : LegacyHeartbeatMaxIntervalMinutes;
        // The forced refresh fires on the first sync after it's due, so allow one more interval, plus slack.
        return DateTimeOffset.UtcNow - seen <= TimeSpan.FromMinutes(Math.Max(refresh, interval) + 2 * interval + 2);
    }
}
