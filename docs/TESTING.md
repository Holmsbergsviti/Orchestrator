<!--
  FILE PURPOSE (in plain terms): A hands-on guide to testing everything added for
  per-machine control — the targeting logic, the heartbeats, and the operator console —
  in three layers, from "no fleet needed" up to a full end-to-end run.
-->
# Testing Guide

There are three layers. Do them in order — each needs more setup than the last.

| Layer | What it proves | Where it runs | Needs a Windows PC? |
|-------|----------------|---------------|---------------------|
| A. Unit tests | Targeting + heartbeat **logic** | any OS | no |
| B. Console-only | The **console** reads the fleet and writes readable targets | any OS (e.g. your Mac) | no (fake a heartbeat) |
| C. End-to-end | A real **agent** reports in and obeys targeting | agent on Windows, console anywhere | yes |

---

## What was added (recap of everything since the start)

**In the code**
- **Targeting:** a `target` field on each manifest program (string or array; hostname/id/"all").
  The agent filters the manifest to the current machine and *uninstalls* anything no longer
  targeted at it.
- **Heartbeats:** each agent commits `state/<machineId>.json` to a **`fleet-state`** branch
  (auto-created), only when something changes or every ~6h.
- **Operator console:** a cross-platform local web UI (`src/Orchestrator.Console`) that reads
  the fleet + manifest from a local clone and commits targeting/label edits back.
- New settings: `ReportState`, `FleetStateBranch`, `HeartbeatMaxIntervalMinutes`.

**What you have to set up to use it**
1. A **control repo** on GitHub (private) with `manifest.json` + `fleet.json` on `main`.
2. A GitHub **token with write access** (read-only works only if you set `ReportState:false`).
3. Each agent installed/run with that token.
4. A **local clone** of the control repo on the PC where you run the console.

---

## Layer A — unit tests (5 minutes, any OS)

```bash
dotnet test
```
Expect green for `TargetingTests` (targeting/filter rules) and `HeartbeatTests` (when a
heartbeat should be committed). This proves the logic without any GitHub or fleet.

---

## Layer B — console only, no agent (test on your Mac)

This exercises the console, the fleet view, and hostname-target writing by **faking** a
machine's heartbeat.

### 1. Make a control repo and push `main`
```bash
# create an empty PRIVATE repo on GitHub first, then:
git clone https://github.com/<you>/<control-repo>.git
cd <control-repo>
cp -R /path/to/Orchestrator/repo-template/* .
git add . && git commit -m "seed control repo" && git push -u origin main
```
`manifest.json` from the template already has a few sample programs — that's enough for the
console to show rows. (Checksums don't matter here; the console only reads id/name/version/
status/target.)

### 2. Fake a machine heartbeat on the `fleet-state` branch
```bash
git checkout -b fleet-state main
mkdir -p state
cat > state/11111111-1111-1111-1111-111111111111.json <<JSON
{
  "machineId": "11111111-1111-1111-1111-111111111111",
  "hostname": "test-pc",
  "os": "Fake OS (layer B test)",
  "agentVersion": "1.0.0.0",
  "lastSeenUtc": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "syncIntervalMinutes": 3,
  "lastSyncSuccess": true,
  "manifestVersion": "1.0",
  "appliedProgramIds": [],
  "lastError": null
}
JSON
git add state && git commit -m "fake heartbeat" && git push -u origin fleet-state
git checkout main
```
(The fresh `lastSeenUtc` makes it show **online**. An old timestamp just shows offline.)

### 3. Run the console against the clone
```bash
cd /path/to/Orchestrator/src/Orchestrator.Console
dotnet run -- /path/to/<control-repo>
```
It opens `http://localhost:5080`.

### 4. What to check
- **Machines** table shows `test-pc`, online, "0 program(s)".
- **Targeting** grid lists the manifest's active programs, one column per machine.
- Type a **label** (e.g. "Layer B box"), tick one program **only** for `test-pc`
  (turn its **All** toggle off first), then **Save & push**. A toast shows a commit id.
- Verify the write is readable and correct:
  ```bash
  cd /path/to/<control-repo> && git fetch
  git show origin/main:manifest.json   # the program now has "target": "test-pc"  (a hostname!)
  git show origin/main:fleet.json      # labels: { "1111...": "Layer B box" }
  ```
- Flip a program's **All** back on and Save → its `target` disappears from the manifest.

If all that works, pieces 1 (schema), 3, and 4 are good. Clean up by deleting the
`fleet-state` branch (or leave it; a real agent will overwrite the file).

---

## Layer C — full end-to-end (real agent on Windows)

### 1. Build / run the agent on a Windows box
You can run it **without installing a service** for testing:
```powershell
# on Windows, in a checkout of this repo
cd src\Orchestrator.Service
# edit appsettings.json first: set RepoOwner, RepoName, Branch, GitHubToken (WRITE token),
# and (optional) RootPath to a writable temp folder to avoid needing admin, e.g.
#   "RootPath": "C:\\Temp\\Orch"
dotnet run -- run
```
`-- run` starts the sync loop in the console (the bare, no-arg form would try to *install*).
It syncs immediately, then every `SyncIntervalMinutes` (default 3).

> Registering a program to run at startup writes to `HKLM\...\Run` (or a Scheduled Task) and
> needs **admin**. For a no-admin test, use programs with `"runAtStartup": false` and a
> `RootPath` you can write to.

For a real install instead: `scripts\install.ps1 -RepoOwner <you> -RepoName <control-repo> -Token <write-token>`.

### 2. Verify the heartbeat
Within a cycle, check the control repo:
```bash
git fetch
git ls-tree --name-only origin/fleet-state state/    # a state/<real-guid>.json appears
git show origin/fleet-state:state/<real-guid>.json    # hostname, lastSeenUtc, applied ids
```
The console (Layer B, now pointed at the real fleet) will show the **real** machine by its
hostname. Give it a label.

### 3. Verify targeting takes effect
- In the console, target a program **at** this machine and Save. Within one cycle the agent
  logs `Manifest v… : N active in manifest, 1 apply to this machine (HOSTNAME)` and installs
  it (check `RootPath\programs\...`).
- Now **untarget** it (uncheck this machine) and Save. Next cycle the agent **uninstalls** it
  — the folder and any startup entry are removed. This is the key behavior: retargeting away
  cleans up.
- Target a program at a *different* hostname only → this machine does **not** install it.

### 4. Read-only token check (optional)
Set `ReportState: false` (or use a read-only token) and confirm the agent still syncs and
logs a single warning about not writing — no crashes, no heartbeat.

---

## Quick troubleshooting
- **Console: "No manifest.json found on origin/main"** → you didn't push `main`, or the clone's
  remote/branch names differ (set `Console:Remote` / `Console:MainBranch`).
- **Console: "No machines have reported yet"** → no `state/*.json` on `fleet-state` (do Layer B
  step 2, or wait for a real agent with a write token).
- **Console save: "uncommitted changes" / "could not fast-forward"** → the clone is dirty or
  `main` diverged; `git status` / `git pull --ff-only` in the clone, then retry.
- **Agent: heartbeat warning about write access** → the token is read-only; grant
  Contents: Read **and write**, or set `ReportState: false`.
- **Agent tries to install itself when you run it** → you ran it with no args; pass `run`.
