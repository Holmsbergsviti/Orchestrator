<!--
  FILE PURPOSE (in plain terms): How to run the operator console — the small program you
  run on your own PC that shows the whole fleet and lets you control each machine
  individually, then commits your changes back to GitHub.
-->
# Operator Console

The console is a **local web UI** you run on your own machine. It shows every machine that
has reported in, lets you rename them and choose (with a checkbox grid) which machines run
which program, then **commits and pushes** those changes to your control repo. The agents
pick them up on their next sync.

Nothing is hosted anywhere — it drives a **local git clone** of your control repo using the
`git` you already have installed, so it uses your existing GitHub login.

## How it fits together
```
   your PC                          GitHub (control repo)            each fleet machine
┌───────────────┐   push edits    ┌─────────────────────┐  pull    ┌──────────────────┐
│ orchestrator- │ ──────────────▶ │ main: manifest.json │ ───────▶ │ orchestrator-    │
│ console (web) │                 │       fleet.json    │          │ service (agent)  │
│               │ ◀────────────── │ fleet-state:        │ ◀─────── │ writes heartbeat │
└───────────────┘   read fleet    │   state/<id>.json   │  report  └──────────────────┘
```
- **main** — `manifest.json` (programs + `target`) and `fleet.json` (friendly names). The
  console reads and writes these.
- **fleet-state** — `state/<machineId>.json` heartbeats. Agents write them; the console only
  reads them. See [heartbeats](#how-machines-appear).

## Prerequisites
- **git** installed and on your PATH.
- A **local clone** of your control repo, checked out on `main`:
  ```bash
  git clone https://github.com/<you>/<control-repo>.git
  ```
  Make sure `git fetch` / `git push` work in that clone without prompting (use a credential
  helper or an SSH remote). The console runs whatever push auth your clone already has.
- The **.NET 8 SDK** to run it (`dotnet run`), or a published build.
- At least one machine must have **reported a heartbeat** before it can be targeted
  individually — see below.

## Run it
```bash
cd src/Orchestrator.Console
dotnet run -- /path/to/your/control-repo
# or set Console:ControlRepoPath in appsettings.json and just: dotnet run
```
It prints the local URL (default `http://localhost:5080`) and opens your browser.

### Settings (`appsettings.json`)
| Key | Meaning | Default |
|-----|---------|---------|
| `Console:ControlRepoPath` | Local path to your cloned control repo (**required**) | — |
| `Console:Remote` | Git remote to fetch/push | `origin` |
| `Console:MainBranch` | Branch with `manifest.json` + `fleet.json` | `main` |
| `Console:FleetStateBranch` | Branch with the heartbeats | `fleet-state` |
| `Console:OpenBrowser` | Open the browser on start | `true` |
| `Urls` | Address the UI listens on | `http://localhost:5080` |

## Using it
- **Machines** — every machine that has reported, with an online/offline dot, OS, agent
  version, last-seen, how many programs it's running, and last sync status. Type a **label**
  to give a machine a friendly name (stored in `fleet.json`).
- **Targeting** — a grid of programs (rows) × machines (columns). Tick a box to run that
  program on that machine. The **All** toggle per program means "every machine, including
  ones that report in later" (it clears the program's `target`); turn it off to pick an
  explicit set.
- **Save & push** — writes `manifest.json` + `fleet.json`, commits, and pushes to `main`.
  The button enables only when you've changed something. The page reloads from GitHub after
  a successful save.

Targets are written as **machine ids** (stable GUIDs), so renaming a computer never breaks
targeting. The grid shows friendly labels; the manifest stores ids.

## How machines appear
Each agent commits `state/<machineId>.json` to the `fleet-state` branch when its situation
changes (and at least every few hours so "last seen" stays fresh). A machine shows up in the
console only **after its first heartbeat**, which requires:
- the agent's GitHub token to have **write** access to the repo, and
- `ReportState` left at its default (`true`) on that machine.

If the fleet-state branch is empty, the console says "No machines have reported yet."

## Safety & conflicts
- The console **only** changes each program's `target` field and `fleet.json` — every other
  manifest field is preserved exactly (edits go through a JSON DOM, not a re-serialize).
- Before saving it fetches and **fast-forwards** `main` to the remote. If your clone has
  uncommitted changes, or `main` has diverged, it refuses and tells you — fix the clone and
  retry. If a push is rejected because the remote moved, reload (which re-fetches) and save
  again.
