# GitHub-Based Windows Orchestrator

A lightweight Windows service that syncs programs (`.exe`, `.bat`, `.ps1`, `.vbs`, `.py`)
from a central GitHub repository. It fetches a `manifest.json`, then installs, updates,
and removes programs across a fleet of machines automatically. Delete a program from the
manifest and it uninstalls everywhere on the next sync.

- **Zero infrastructure** — GitHub is the control plane.
- **Self-contained** — publishes to a single `.exe`, no .NET runtime needed on targets.
- **Verified** — every download checked against a SHA256 in the manifest.
- **Silent** — runs as a SYSTEM Windows Service, no UI.

## Repository layout

```
Orchestrator/
├─ Orchestrator.sln
├─ src/Orchestrator.Service/       # the service (C# / .NET 8)
│  ├─ Models/                      # Manifest, ProgramEntry, config, plan
│  ├─ Services/                    # GitHub, checksum, registry, manifest, sync
│  ├─ Program.cs / Worker.cs       # host + background loop
│  └─ appsettings.json             # default config (overwritten at install)
├─ scripts/                        # publish / install / uninstall / checksum
├─ repo-template/                  # what YOUR control repo should look like
│  ├─ manifest.json
│  └─ schemas/manifest-schema.json
└─ docs/                           # SETUP, ADDING-PROGRAMS, TROUBLESHOOTING
```

There are **two repos** in this design:
1. **This repo** — the orchestrator service source code.
2. **Your control repo** (private) — holds `manifest.json` + the `/programs` files.
   Start it from [`repo-template/`](repo-template/).

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
6. Logs everything to `C:\Orchestrator\logs\log-YYYY-MM-DD.txt`.

See [docs/SETUP.md](docs/SETUP.md), [docs/ADDING-PROGRAMS.md](docs/ADDING-PROGRAMS.md),
and [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md).

## Configuration (`appsettings.json`)

| Key | Meaning | Default |
|-----|---------|---------|
| `RootPath` | Install/data directory | `C:\Orchestrator` |
| `RepoOwner` / `RepoName` | Control repo | — |
| `Branch` | Branch to read | `main` |
| `ManifestPath` | Manifest path in repo | `manifest.json` |
| `GitHubToken` | PAT with `repo:read` (empty = public) | `""` |
| `SyncIntervalMinutes` | Minutes between cycles | `60` |
| `RegistryEntryPrefix` | Namespaces Run entries | `Orch_` |

## Uninstall
```powershell
.\scripts\uninstall.ps1        # stops + deletes service, removes files + Orch_* startup keys
```

## Notes & limits (Phase 1)
- Windows-only (Registry + service host). Build/run the Windows-specific paths on Windows.
- Runs as **SYSTEM** — programs launched at startup inherit high privilege.
- All machines get all `active` programs (per-machine targeting is Phase 2 — MachineID is
  already generated and stored in `config.json` for it).
- Rollback = revert the manifest commit; the next sync converges to it.

## Requirements
- Targets: Windows 10/11 or Server 2016+ (Win7 SP1+ in theory).
- Build: .NET 8 SDK. Targets need **no** runtime (self-contained publish).
- Outbound HTTPS to `api.github.com`.
