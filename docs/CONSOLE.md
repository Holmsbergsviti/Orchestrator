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
- **Delete program** (in a program's Settings) — removes the entry from the manifest
  entirely (and its file from the repo) and pushes. Every machine that has it uninstalls
  it on the next sync; machines that never had it are unaffected. This is the hard delete —
  different from unticking all machines, which just deactivates it but keeps a `deleted` row.
- **Run now** (in Settings) — runs the program once on each targeted machine's next sync, in
  the logged-in user's session.
- **Shut down / Restart** (Machines table, Power column) — sends a one-off power command to a
  machine. The agent runs it forced (`/f`) on its next sync, after a 5-second warning. Written
  to `commands.json` with a nonce so it runs exactly once and never loops after reboot.
- **Wake / Wake all** (Machines table) — Wake-on-LAN. The agent can't power a machine on (it's
  off), so a designated **waker** does it: install one always-on machine per network segment
  with `-IsWaker` (or `"IsWaker": true` in its appsettings), enable WoL in each target's
  BIOS/NIC, and the waker broadcasts magic packets to the targets' MACs (reported in their
  heartbeats). Machines with no reported MAC yet are skipped.
- **Screenshot** (Machines table, Power column) — requests a screen capture on that machine's
  next sync. Written to `commands.json` with a nonce, same one-shot pattern as shutdown/restart.
  The service itself runs in Windows session 0 (no desktop), so it only schedules the actual
  capture into the **logged-on user's interactive session** — it does nothing if no one is
  signed in there. Once uploaded (to `screenshots/<machineId>/` on the `fleet-state` branch,
  alongside the heartbeats), a **View** link appears next to the button to open the latest one.
- **Remote** (Machines table, Power column) — starts a **live remote-control session**: opens a
  viewer tab that shows that machine's screen at roughly 5 fps. Needs one-time setup on both
  ends first (`Console:AccessToken` + a certificate here, `Orchestrator:RelayUrl` there) —
  see [SETUP.md](SETUP.md#7-optional-live-remote-control). Requirements and behaviour:
  - The session starts on the machine's **next sync**, so the viewer waits (with a countdown)
    for up to 15 minutes. Lower that machine's `SyncIntervalMinutes` for a faster start.
  - Someone must be **signed in** on the target — the capture runs in their desktop session.
  - A red **"Remote control active — click to end"** banner appears on the target for the whole
    session. It can't be turned off, and clicking it ends the session immediately — that
    override always wins over the console.
  - Sessions end at `RemoteSessionMaxMinutes` (default 30) whatever the console is doing.
  - Pending sessions live in the console's memory, so **restarting the console cancels them**;
    just press Remote again.
  - Pressing Remote again on a machine that already has a session reopens **that** viewer
    rather than starting a second one.
  - **Mouse and keyboard drive the remote machine.** Click, drag, scroll and type on the
    canvas and it happens over there. While **Input: on**, this tab also swallows your own
    browser shortcuts (Ctrl+W, F5, Ctrl+T) so they go to the remote machine instead — switch
    to **Input: off** to get your browser back without ending the session.
  - Input can't reach an elevated window, a UAC prompt, or the lock screen. Windows blocks
    synthetic input to those on purpose; the screen keeps streaming, clicks just do nothing.
  - Anything still held down (a modifier, a mouse button) is released automatically when the
    session ends, however it ends.
- **Save & push** — writes `manifest.json` + `fleet.json`, commits, and pushes to `main`.
  The button enables only when you've changed something. The page reloads from GitHub after
  a successful save.

Targets are written as **hostnames**, so `manifest.json` stays human-readable
(`"target": ["olegs-laptop", "desktop-abc123"]`). The grid shows your friendly labels; the
manifest stores the machine's real hostname. (Note: if two machines share a hostname, a
hostname target hits both — rename one to disambiguate.)

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
