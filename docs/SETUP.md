<!--
  FILE PURPOSE (in plain terms): A step-by-step guide for getting the Orchestrator
  running for the first time — building the service, preparing your control repo,
  creating a GitHub token, installing on target machines, and verifying it works.
-->
# Setup Guide

## 0. Prerequisites
- A dev/build machine with the **.NET 8 SDK**.
- A **private GitHub repo** to act as the control plane.
- A **Personal Access Token** (fine-grained or classic) with **read access to repo contents**.

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
- Fine-grained: grant the control repo **Contents: Read-only**.
- Classic: `repo` scope.

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

## Public repos
Omit `-Token`. The service calls the GitHub API anonymously (lower rate limit, 60/hr).

## Updating the service itself
Re-run `publish.ps1`, then `install.ps1` again — it stops the service, overwrites the
binaries, and restarts.
