# GitHub-Based Windows Orchestrator

A lightweight Windows service that syncs programs (`.exe`, `.bat`, `.ps1`, `.vbs`, `.py`)
from a central GitHub repository. It fetches a `manifest.json`, then installs, updates,
and removes programs across a fleet of machines automatically. Delete a program from the
manifest and it uninstalls everywhere on the next sync — or **`target` a program at
specific machines** to control each PC individually.

- **Zero infrastructure** — GitHub is the control plane (two-way: agents also report back).
- **Per-machine control** — scope any program to specific machines by hostname or id.
- **Operator console** — an optional local web UI to see the fleet and drive it, instead of
  hand-editing JSON. See [docs/CONSOLE.md](docs/CONSOLE.md).
- **Self-contained** — publishes to a single `.exe`, no .NET runtime needed on targets.
- **Verified** — every download checked against a SHA256 in the manifest.
- **Silent** — runs as a SYSTEM Windows Service, no UI.
- **Self-updating** — agents converge on the published build by hash and swap themselves via a
  one-shot SYSTEM scheduled task.
- **Fleet commands** — shutdown / restart / Wake-on-LAN / screenshot, each written once with a
  nonce so it runs exactly once.
- **Live remote control** — optional, consent-visible screen + input session over a WebSocket
  relay, with a mandatory on-screen banner and an audit log on the target.

## Repository layout

```
Orchestrator/
├─ Orchestrator.sln
├─ src/Orchestrator.Service/       # the agent — a Windows service (C# / .NET 8)
│  ├─ Models/                      # Manifest, ProgramEntry, Heartbeat, config, plan
│  ├─ Services/                    # GitHub, checksum, registry, manifest, sync, fleet reporter
│  ├─ Program.cs / Worker.cs       # host + background loop
│  └─ appsettings.json             # default config (overwritten at install)
├─ src/Orchestrator.Console/       # the operator console — a local web UI (cross-platform)
├─ scripts/                        # publish / install / uninstall / checksum
├─ repo-template/                  # what YOUR control repo should look like
│  ├─ manifest.json  fleet.json
│  └─ schemas/manifest-schema.json
└─ docs/                           # SETUP, ADDING-PROGRAMS, CONSOLE, TROUBLESHOOTING
```

There are **two repos** in this design:
1. **This repo** — the orchestrator agent + console source code.
2. **Your control repo** (private) — holds `manifest.json`, `fleet.json`, and the
   `/programs` files on `main`; the agents also auto-create a `fleet-state` branch and
   commit their heartbeats there. Start it from [`repo-template/`](repo-template/).

## Quick start

### 1. Build the service (dev machine with .NET 8 SDK)
```powershell
cd scripts
.\publish.ps1          # -> scripts\publish\orchestrator-service.exe
```

### 2. Create your control repo
Copy `repo-template/` into a new **private** GitHub repo. Add your program files
under `programs/<name>/<version>/`, generate checksums, and list them in `manifest.json`:
```powershell
.\scripts\gen-checksum.ps1 -Path .\programs\my-app\v1.0\my-app.exe
```

### 3. Install on a target machine (elevated PowerShell)
```powershell
.\scripts\install.ps1 -RepoOwner acme -RepoName control-repo -Token ghp_xxx
```
The service starts, runs an immediate sync, then repeats every 60 minutes.

## How sync works

Every cycle the service:
1. Fetches `manifest.json` via the GitHub Contents API (works for private repos with a PAT).
2. Diffs it against the last-known manifest + on-disk state (`cache/`).
3. Downloads new/changed files from GitHub, **verifies SHA256**, installs atomically.
4. Registers `runAtStartup` programs under `HKLM\...\CurrentVersion\Run` (prefix `Orch_`).
5. Removes any program marked `"status": "deleted"` — files and startup entry.
6. Logs everything to `C:\Windows\Orch\logs\log-YYYY-MM-DD.txt`.

See [docs/SETUP.md](docs/SETUP.md), [docs/ADDING-PROGRAMS.md](docs/ADDING-PROGRAMS.md),
and [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md).

## Controlling each machine individually

By default every machine runs every `active` program. Add a `target` to a program to scope
it — omit it (or `"all"`) for everyone:
```json
"target": "olegs-laptop"                       // by hostname
"target": ["olegs-laptop", "DESKTOP-ABC123"]   // a set (hostname and/or machine id)
```
Matching is case-insensitive against each machine's **hostname or machine id**. Retarget a
program away from a machine and it **uninstalls** there on the next sync. Full details in
[docs/ADDING-PROGRAMS.md](docs/ADDING-PROGRAMS.md#target-specific-machines).

## Two-way: heartbeats & the operator console

Each agent reports back by committing `state/<machineId>.json` to a dedicated **`fleet-state`**
branch (auto-created, kept off `main`): hostname, OS, agent version, last-seen, last sync
result, and what it's running. To avoid noise it commits only when something meaningful
changes, or every few hours to refresh "last seen".

The **operator console** ([docs/CONSOLE.md](docs/CONSOLE.md)) is a local web UI you run on
your own PC. It reads the manifest + heartbeats from a local clone, shows your fleet by
friendly name with a program-vs-machine checkbox grid, and commits/pushes your changes back:
```bash
cd src/Orchestrator.Console && dotnet run -- /path/to/control-repo
```
Heartbeats and the console need the agent token to have **write** access. With a read-only
token, set `ReportState: false` — the agent logs one warning and keeps syncing normally.

## Fleet commands & remote control

The console writes one-shot commands to `commands.json` on `main`, each tagged with a nonce so an
agent runs it exactly once and never loops after a reboot:

- **Shutdown / restart** — executed on the agent's next sync.
- **Wake / Wake all** — Wake-on-LAN. A designated always-on **waker** machine per network segment
  broadcasts magic packets to the MAC addresses the targets reported in their heartbeats.
- **Screenshot** — the agent captures the signed-in user's screen and uploads it to the
  `fleet-state` branch.
- **Remote** — a live remote-control session: the agent opens an *outbound* WebSocket to the
  console's relay, streams frames out and injects the operator's mouse/keyboard events in.
  A red **"Remote control active — click to end"** banner is always shown on the target and cannot
  be disabled; sessions are time-boxed (`RemoteSessionMaxMinutes`, default 30) and every session is
  recorded in `logs\remote-sessions.json` on the machine. Details in [docs/CONSOLE.md](docs/CONSOLE.md).

## Agent self-update

Each sync the agent reads `agent.json` from the control repo and compares the published build's
SHA256 with the hash of the exe it is **actually running** — not a self-reported version string, so
rolled-back or half-updated machines still converge. A mismatching download is treated as hostile,
not retried. Because a running service cannot overwrite itself, the swap is handed to `install.ps1`
through a one-time SYSTEM scheduled task that stops the service, replaces the binary and restarts it.
The `Publish agent` workflow builds, tests and publishes the win-x64 binary and records the release.

## Changing names & paths — `defaults.json`

The fixed names and paths (install root, service name, exe name, registry key/prefix,
default branch/interval, etc.) live in **one file at the repo root: [`defaults.json`](defaults.json)**.
Edit a value there and it flows everywhere:

- the **C# service** embeds `defaults.json` at build time and reads it via `OrchestratorDefaults.cs`
  (so rebuild after changing it),
- the **PowerShell scripts** read it at runtime, and
- the exe's **`<AssemblyName>`** in the csproj is derived from `exeName` at build time, so even
  renaming the exe is a one-place change.

The per-machine `appsettings.json` written at install time still wins at runtime;
`defaults.json` supplies the defaults behind it.

## Configuration (`appsettings.json`)

| Key | Meaning | Default (from `defaults.json`) |
|-----|---------|---------|
| `RootPath` | Install/data directory | `C:\Windows\Orch` |
| `RepoOwner` / `RepoName` | Control repo | — |
| `Branch` | Branch to read | `main` |
| `ManifestPath` | Manifest path in repo | `manifest.json` |
| `GitHubToken` | PAT — `repo` write for heartbeats, read for sync-only (empty = public) | `""` |
| `SyncIntervalMinutes` | Minutes between cycles | `60` |
| `RegistryEntryPrefix` | Namespaces Run entries | `Orch_` |
| `ReportState` | Commit a heartbeat to `fleet-state` (needs a writable token) | `true` |
| `FleetStateBranch` | Branch heartbeats are committed to | `fleet-state` |
| `HeartbeatMaxIntervalMinutes` | Force a heartbeat at least this often (also how long an idle machine still counts as online) | `30` |

## Uninstall
```powershell
.\scripts\uninstall.ps1        # stops + deletes service, removes files + Orch_* startup keys
```

## Notes & limits
- The **agent** is Windows-only (Registry + service host). The **console** is cross-platform
  (net8.0) — run it on Windows, macOS, or Linux.
- Runs as **SYSTEM** — programs launched at startup inherit high privilege.
- Per-machine targeting is implemented (`target`); a machine must report **one heartbeat**
  before the console can target it individually (write token + `ReportState: true`).
- Rollback = revert the manifest commit; the next sync converges to it.
- The console needs `git` on your PATH and a local clone whose `push` is already authenticated.

## Development & tests

```bash
dotnet build Orchestrator.sln     # agent + console, warnings treated as errors
dotnet test                       # xUnit suite (manifest diffing, targeting, checksums, heartbeats, ...)
```

The service targets `net8.0-windows` but compiles and unit-tests on macOS/Linux; only the Windows
runtime paths (registry, WMI, scheduled tasks, process launch) need a Windows machine. The install
scripts are covered by Pester tests in CI (`.github/workflows/test-scripts.yml`).

## Requirements
- Targets: Windows 10/11 or Server 2016+ (Win7 SP1+ in theory).
- Build: .NET 8 SDK. Targets need **no** runtime (self-contained publish).
- Outbound HTTPS to `api.github.com`.
