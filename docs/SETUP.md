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

Drop `-RelayCertThumbprint` if the console uses a certificate from a real CA. `install.ps1`
rewrites `appsettings.json` from scratch every run, so pass these as parameters rather than
hand-editing the file — an edit would be wiped on the next upgrade.

Then use **Remote** in the console (see [CONSOLE.md](CONSOLE.md)). A session starts on that
machine's next sync, so lower `-IntervalMinutes` on machines you want to reach quickly.

## Public repos
Omit `-Token`. The service calls the GitHub API anonymously (lower rate limit, 60/hr).
Heartbeats need a writable token, so with a public/anonymous setup set `ReportState: false`.

## Updating the service itself
Re-run `publish.ps1`, then `install.ps1` again — it stops the service, overwrites the
binaries, and restarts.
