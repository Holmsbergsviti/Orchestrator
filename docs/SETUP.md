<!--
  FILE PURPOSE (in plain terms): A step-by-step guide for getting the Orchestrator
  running for the first time — building the service, preparing your control repo,
  creating a GitHub token, installing on target machines, and verifying it works.
-->
# Setup Guide

## 0. Prerequisites
- A dev/build machine with the **.NET 8 SDK**.
- A **private GitHub repo** to act as the control plane.
- A **Personal Access Token** (fine-grained or classic) for repo contents:
  - **Read and write** if you want machines to report heartbeats (the default — this is what
    makes the [operator console](CONSOLE.md) and per-machine control usable).
  - **Read-only** is enough if you only push the same programs to every machine and don't
    need heartbeats; in that case set `ReportState: false` in `appsettings.json` (otherwise
    each agent logs one warning that it can't write, and keeps syncing).

## 1. Build the service
```powershell
cd scripts
.\publish.ps1
```
Output: `scripts\publish\` containing `orchestrator-service.exe` (self-contained) and
`appsettings.json`.

## 2. Prepare the control repo
```powershell
git clone https://github.com/<you>/control-repo.git
cd control-repo
# copy the template
cp -r <this-repo>/repo-template/* .
mkdir -p programs/my-app/v1.0
cp my-app.exe programs/my-app/v1.0/
```
Generate a checksum and add an entry to `manifest.json`:
```powershell
.\scripts\gen-checksum.ps1 -Path programs/my-app/v1.0/my-app.exe
```
Commit + push.

## 3. Create a GitHub token
GitHub → Settings → Developer settings → Personal access tokens.
- Fine-grained: grant the control repo **Contents: Read and write** (Read-only if you've set
  `ReportState: false`).
- Classic: `repo` scope.

On its first heartbeat each agent auto-creates the **`fleet-state`** branch and writes
`state/<machineId>.json` to it. You don't need to create that branch yourself.

## 4. Install on each target (Administrator PowerShell)
```powershell
.\scripts\install.ps1 `
    -RepoOwner  <you> `
    -RepoName   control-repo `
    -Token      ghp_xxx `
    -IntervalMinutes 60
```
The installer:
- copies binaries to `C:\Windows\Orch`,
- writes `appsettings.json` with your repo + token,
- locks the folder to SYSTEM + Administrators,
- creates the `GitHubOrchestrator` service (Automatic start, auto-restart on failure),
- starts it (first sync runs immediately).

## 5. Verify
```powershell
Get-Service GitHubOrchestrator
Get-Content C:\Windows\Orch\logs\log-*.txt -Tail 40
```

## 6. (Optional) Run the operator console
On your own PC, drive the fleet from a local web UI instead of hand-editing `manifest.json`:
```bash
cd src/Orchestrator.Console
dotnet run -- /path/to/your/control-repo   # a local clone, checked out on main
```
It opens `http://localhost:5080`, shows every machine that has reported, and lets you rename
them and pick which machines run which program — then commits and pushes for you. Full guide:
[docs/CONSOLE.md](CONSOLE.md).

## 7. (Optional) Live remote control

Screenshots and commands travel through GitHub, but a live screen can't — it needs a direct
connection. The console itself hosts the relay, and each agent dials **out** to it, so the
fleet machines need no inbound ports. Only the console's own port has to be reachable.

Nothing here is auto-discovered: an agent that doesn't know the relay address simply never
starts a session. Configure both ends.

### a. Expose the console

Bound to `localhost` (the default) the console can only relay for an agent on the *same* PC.
To control other machines, bind it wider — which requires an access token **and** HTTPS, and
the console refuses to start otherwise:

```json
{
  "Console": {
    "AccessToken": "<a long random string — this is your fleet's password>",
    "CertPfxPath": "C:\\path\\to\\console.pfx",
    "CertPfxPassword": "<pfx password>"
  },
  "Urls": "https://0.0.0.0:5080"
}
```

No certificate yet? A self-signed one is fine — the agents pin it by thumbprint (see below),
which is stricter than ordinary CA trust:

```bash
openssl req -x509 -newkey rsa:2048 -keyout key.pem -out cert.pem -days 825 -nodes \
  -subj "/CN=orchestrator-console"
```

```bash
openssl pkcs12 -export -out console.pfx -inkey key.pem -in cert.pem
```

Your **browser** will warn about a self-signed certificate the first time; accept it once.
Open the firewall for the port, and if you're reaching the console from outside your LAN,
forward that port to it.

### b. Point the agents at it

Start the console and it prints exactly what to use:

```text
Remote control — settings for the agents (see docs/SETUP.md):
  Orchestrator:RelayUrl            = wss://<this-pc-ip-or-hostname>:5080
  Orchestrator:RelayCertThumbprint = THUMBPRINT-OF-YOUR-OWN-CERT-40-HEX-CHARS
```

> Use **your** console's output. The thumbprint identifies one specific certificate — the
> one you generated above — so a value copied from any example (including this page) will be
> rejected by the agent, which is exactly what pinning is for.

Pass both when installing each machine you want to control (substitute the real address for
the placeholder — only you know whether agents reach this PC by LAN IP, hostname, or a public
name):

```powershell
.\scripts\install.ps1 -RepoOwner <you> -RepoName control-repo -Token ghp_xxx `
    -RelayUrl wss://192.168.1.20:5080 -RelayCertThumbprint THUMBPRINT-OF-YOUR-OWN-CERT
```

Drop `-RelayCertThumbprint` if the console uses a certificate from a real CA. Pass these as
parameters rather than hand-editing `appsettings.json` — the installer rewrites that file.

**Changing a setting later** doesn't need any of this again. The installer leaves a copy of
itself and `defaults.json` in the install folder, and preserves every setting you don't pass:

```powershell
powershell -ExecutionPolicy Bypass -File C:\Windows\Orch\install.ps1 -RelayCertThumbprint <new value>
```

That re-pins a regenerated certificate and restarts the service, keeping the repo, token,
interval and relay address exactly as they were. Only a *first* install needs the full list.

Then use **Remote** in the console (see [CONSOLE.md](CONSOLE.md)). A session starts on that
machine's next sync, so lower `-IntervalMinutes` on machines you want to reach quickly.

## 8. (Optional) Automatic agent updates

With this set up, pushing to `main` rebuilds the agent and every machine installs it within one
sync interval — no visiting machines, no reinstall commands.

**How the trust works.** The binary is published to the **public** code repo's `dist` branch,
but its SHA-256 is written to `agent.json` in your **private control repo**. An agent installs a
download only if it hashes to that value. So getting code onto every machine as SYSTEM requires
both repos, not just the public one. An agent that can't verify a build refuses it.

### Set it up (once)

The workflow needs to write to your control repo, which it can't do with its own token:

1. Create a fine-grained PAT with **Contents: Read and write** on the control repo only.
2. In the **code** repo → Settings → Secrets and variables → Actions:
   - **Secret** `CONTROL_REPO_TOKEN` = that PAT.
   - **Variable** `CONTROL_REPO` = `<you>/<control-repo>`, e.g. `acme/orchestrator-control`.

That's it. Every push to `main` that touches `src/`, `defaults.json` or the solution now runs
the tests, cross-builds the win-x64 exe, force-pushes it to `dist`, and records it in
`agent.json`. Agents pick it up on their next sync and restart themselves into the new build.

Machines need **one** normal install first (§7) — self-update replaces the binary in place, so
something has to put it there to begin with.

### The brakes

`dotnet test` gates every publish, but a green test run is not proof a build is good, and there
is no automatic rollback. Two ways to stop the fleet:

- **`"enabled": false` in `agent.json`** — set it in the control repo and every machine stops
  updating on its next sync, without unpublishing anything.
- **`-AutoUpdate $false`** at install time pins one machine to its current build. Useful to keep
  one known-good machine out of an update while you check a new build on another.

To recover from a bad build that's already out: push a fix (the fleet takes the next build the
same way), or set `enabled: false` and reinstall affected machines with §7's bootstrap command,
which downloads whatever `dist` currently holds.

## Public repos
Omit `-Token`. The service calls the GitHub API anonymously (lower rate limit, 60/hr).
Heartbeats need a writable token, so with a public/anonymous setup set `ReportState: false`.

## Updating the service itself
Re-run `publish.ps1`, then `install.ps1` again — it stops the service, overwrites the
binaries, and restarts.
