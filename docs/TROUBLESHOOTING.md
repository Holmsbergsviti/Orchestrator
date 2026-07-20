# Troubleshooting

Logs: `C:\Orchestrator\logs\log-YYYY-MM-DD.txt` (one per day).
Structured history: `C:\Orchestrator\logs\sync-history.json`.
Last-applied manifest: `C:\Orchestrator\cache\local-manifest.json`.

## Service won't start
```powershell
Get-Service GitHubOrchestrator
Get-EventLog -LogName Application -Source GitHubOrchestrator -Newest 20   # if present
Get-Content C:\Orchestrator\logs\log-*.txt -Tail 50
```
- Bad `appsettings.json` (invalid JSON) → the host logs a fatal on startup. Fix and
  `Restart-Service GitHubOrchestrator`.

## `Checksum mismatch. expected=... actual=...`
The downloaded bytes don't match the manifest hash.
- Wrong or stale checksum in `manifest.json`. Recompute:
  `.\scripts\gen-checksum.ps1 -Path <file>` and update the manifest.
- File replaced in the repo without bumping the checksum.
The install is skipped and retried next cycle; nothing corrupt is written.

## `GitHub path not found`
The `path`/`url` in the manifest doesn't resolve.
- Check the repo-relative `path` is exact (case-sensitive).
- Confirm `Branch` in `appsettings.json` matches where the file lives.

## 401 / 403 from GitHub
- Token missing, expired, or lacks **Contents: Read** on the control repo.
- Rate limited: anonymous (public) = 60 req/hr; authenticated = 5000 req/hr. Add a token
  or raise `SyncIntervalMinutes`.

## Program not launching at boot
Startup registration depends on `runAsAdmin`:
- **`runAsAdmin: false`** → an `HKLM\...\Run` value. Confirm it exists:
  ```powershell
  Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" | Select Orch_*
  ```
  Run entries fire at **user logon** in that user's non-elevated context — headless/no-login
  boxes won't run them. Use `runAsAdmin: true` for those.
- **`runAsAdmin: true`** → a Scheduled Task running as SYSTEM with highest privilege, at boot.
  Confirm it exists:
  ```powershell
  schtasks /Query /TN Orch_my-app /V /FO LIST
  ```
  This fires at boot even with no user logged in.

## A deleted program didn't get removed
- Prefer `status: deleted`; it must stay in the manifest long enough for the machine to
  sync it once. (Removing the entry outright also uninstalls, using the last-known local
  `installPath`, but leaves no `reason` in the logs.)
- Deletion keys off `installPath`; make sure it matches what was installed.
- Cleanup removes both startup mechanisms (Run value and Scheduled Task), so a program that
  had `runAsAdmin` toggled still gets fully unregistered.

## Force an immediate sync
```powershell
Restart-Service GitHubOrchestrator   # first cycle runs on start
```

## Nothing happens / no logs
- Verify outbound HTTPS to `api.github.com` (proxy/firewall).
- Check the folder is writable by SYSTEM (installer sets ACLs; don't tighten further).

## Reset local state (re-sync from scratch)
```powershell
Stop-Service GitHubOrchestrator
Remove-Item C:\Orchestrator\cache\*.json
Start-Service GitHubOrchestrator
```
This re-installs every active program (checksums re-verified).
