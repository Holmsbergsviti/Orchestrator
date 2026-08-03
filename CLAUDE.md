# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
dotnet build Orchestrator.sln          # build both projects (agent + console)
dotnet test                            # run all unit tests (xUnit)
dotnet test --filter FullyQualifiedName~TargetingTests            # one test class
dotnet test --filter FullyQualifiedName~ManifestServiceTests.NewProgram_IsPlannedAsInstall  # one test

# Run the operator console locally (serves http://localhost:5080):
dotnet run --project src/Orchestrator.Console -- /path/to/control-repo-clone

# Run the agent in console mode on Windows (NOT as a service; bare invocation self-installs):
dotnet run --project src/Orchestrator.Service -- run
```

- **The .NET 8 SDK on this machine is user-local at `~/.dotnet` and not on PATH.** Prefix commands
  with `export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"` (or call `~/.dotnet/dotnet`).
- Both projects target **net8.0** but the service is **net8.0-windows** (it uses the registry, WMI,
  scheduled tasks). It still *compiles and unit-tests* on macOS/Linux; only the Windows *runtime*
  paths (registry, `shutdown.exe`, WMI, WoL, launching processes) can't be exercised off Windows.
- No linter is configured. Build warnings are treated as the quality bar (builds are `-warnaserror`-clean).
- Publish the self-contained agent exe: `scripts/publish.ps1`. One-command fleet install: `scripts/bootstrap.ps1`
  (downloads or `-BuildFromSource` builds, then runs `scripts/install.ps1`).

## Architecture: GitHub is the (two-way) control plane

There is **no server**. Everything coordinates through a private **control repo** on GitHub, which is
separate from this code repo:

- **`main` branch** (operator-owned): `manifest.json` (programs + targeting), `fleet.json` (friendly
  machine labels), `commands.json` (shutdown/restart/wake), and the actual program files under `programs/`.
- **`fleet-state` branch** (agent-owned, auto-created): `state/<machineId>.json` heartbeats each agent
  commits (hostname, OS, last-seen, applied program ids, **MAC addresses** for Wake-on-LAN).

Two executables talk only through those files:

- **`src/Orchestrator.Service`** — the **agent**. A Windows service (runs as SYSTEM) that every
  `SyncIntervalMinutes` pulls the manifest, installs/updates/removes programs, executes commands, and
  reports a heartbeat. Reads via the GitHub Contents API (works on private repos with a PAT); writes
  heartbeats via the API PUT path (**needs a write-scoped token**; degrades to read-only + a warning).
- **`src/Orchestrator.Console`** — the **operator console**. A cross-platform ASP.NET app you run on
  your own machine. It drives a **local git clone** of the control repo by shelling out to `git`
  (`GitRepo.cs`) — reads manifest/fleet.json from `origin/main` and heartbeats from `origin/fleet-state`,
  and on save **fast-forwards main, edits, commits, and pushes**. It never uses a PAT; it uses your
  existing git credentials. Agent changes need a reinstall to take effect; console changes are live on
  a rebuild/restart (the page HTML is served from source `wwwroot/`).

### Single source of truth: `defaults.json`
Fixed names/paths (install root, service name, exe name, branch names, intervals) live **only** in
repo-root `defaults.json`. The service embeds it at build time and reads it via `OrchestratorDefaults.cs`;
the exe's `<AssemblyName>` is derived from `defaults.json`'s `exeName` in the csproj; the PowerShell
scripts read it at runtime. Change a value there, rebuild, and it flows everywhere.

## Agent control flow (the parts that span files)

`Worker` loops → `SyncService.RunSyncAsync` is the orchestrator of one cycle:

1. `GitHubClient.GetManifestAsync` fetches the manifest.
2. **`ManifestService.FilterForMachine`** reduces it to *this machine's* view: active programs whose
   `target` doesn't match this machine's hostname/id are turned into `deleted` entries so they get
   **uninstalled locally**. This is how per-machine targeting works — the rest of the pipeline is unchanged.
3. `ManifestService.BuildPlan` diffs the effective manifest against the local baseline cache
   (`cache/local-manifest.json`) + on-disk state to produce install/update/delete/up-to-date actions.
   A version bump *or* a checksum mismatch triggers reinstall.
4. Actions run; then run-now requests, admin commands, and a heartbeat (`FleetReporter`).

**Startup launching is gated (important).** Startup entries do NOT point at the program. `StartupManager`
(→ `RegistryService` for HKLM Run, or `ScheduledTaskService` for `runAsAdmin`) registers
`orchestrator-service.exe run-program <id>`. At logon that lands in `ProgramLauncher` (the `run-program`
verb in `Program.cs`, sharing DI with the host via `ServiceRegistration.cs`), which **re-checks the
current manifest and only launches the program if it's still active + targeted** — so a just-deleted
program never runs at boot, and it launches in the interactive user session.

**Session matters.** The service runs in Windows **session 0** (no interactive desktop/audio):
- `runOnceInstalled` runs there (fine for silent tasks, invisible for UI/audio).
- `runRequest` ("Run now") and startup runs use a **one-time interactive scheduled task**
  (`ScheduledTaskService.RunInteractiveOnce`, InteractiveToken) so they run in the logged-in user's
  session — that's the only way GUI/audio programs are visible.

**Nonce pattern for one-shot actions.** `runRequest` (run-now), `commands` (shutdown/restart), and
`wake` all carry a nonce/id; the agent records executed ids in `config.json` (`CompletedRunRequests`,
`CompletedCommands`, `CompletedWakes`) so each fires exactly once and never loops after reboot.

**Wake-on-LAN.** The agent can't power on an off machine, so a machine installed with `IsWaker=true`
sends WoL magic packets (`WakeSender`) for `wake` requests to the target MACs reported in heartbeats.
Only works within a LAN segment (broadcast) — one waker per broadcast domain.

## Console specifics

- Manifest edits go through a **JSON DOM** (`System.Text.Json.Nodes`), not deserialize/reserialize, so
  only the intended fields change and everything else round-trips untouched.
- Targeting is written as **hostnames** (readable), translated from the stable machine ids the UI tracks.
- Saves refuse a **dirty or diverged** clone (fast-forward-only) to avoid clobbering; surfaced as a UI error.

## Conventions

- Every source file opens with a plain-language "FILE PURPOSE" comment block, and lines carry inline
  comments. Match that density when editing.
- Cross-platform code guards Windows-only calls with `OperatingSystem.IsWindows()` and
  `[SupportedOSPlatform("windows")]`; keep new Windows APIs behind those guards so the console/tests build.
- `docs/` (SETUP, ADDING-PROGRAMS, CONSOLE, TESTING, TROUBLESHOOTING) and `repo-template/` (the starter
  control repo) are user-facing — update them when you change manifest fields or console behavior.
