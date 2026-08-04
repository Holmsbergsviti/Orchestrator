<!--
  FILE PURPOSE (in plain terms): A problem-solving guide. For each common symptom
  (service won't start, checksum mismatch, 401/403, program not launching, etc.) it
  explains the likely cause and the exact commands to diagnose and fix it.
-->
# Troubleshooting

Logs: `C:\Windows\Orch\logs\log-YYYY-MM-DD.txt` (one per day). The service and any interactive
process it launches (run-now, screenshot, remote session) all write here, tagged by process id.
Structured history: `C:\Windows\Orch\logs\sync-history.json`.
Remote-control audit trail: `C:\Windows\Orch\logs\remote-sessions.json`.
Last-applied manifest: `C:\Windows\Orch\cache\local-manifest.json`.

## Service won't start
```powershell
Get-Service GitHubOrchestrator
Get-EventLog -LogName Application -Source GitHubOrchestrator -Newest 20   # if present
Get-Content C:\Windows\Orch\logs\log-*.txt -Tail 50
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

## Remote control: the viewer never shows a picture
The viewer tab says what stage it's stuck at — read that first, then match it below. The
target machine also shows a banner with the reason, and logs it to `logs\log-*.txt` there.

**"Waiting for … to pick up the request"** — the agent hasn't connected yet.
- It only starts on that machine's **next sync**. Wait one `SyncIntervalMinutes`.
- Nobody signed in on that machine? Then it can't run: the capture needs a desktop session,
  and the log says `no interactive user is logged on`.

**Agent log says `remote-session '...' request expired before this sync; skipping`** — the
request sat unclaimed for more than 10 minutes, so the agent discarded it rather than starting
a session long after you asked for one. Just press **Remote** again.
- If it happens *every* time, that machine's `SyncIntervalMinutes` is too close to (or past)
  the 10-minute window, so it can never notice a request in time. Reinstall it with a smaller
  `-IntervalMinutes`; a few minutes is a good value for machines you want to reach.

**No banner ever appears on the target** — the session process quit immediately.
- Almost always `Orchestrator:RelayUrl` is empty there. Check it:
  ```powershell
  Get-Content C:\Windows\Orch\appsettings.json | Select-String Relay
  ```
  Fix by re-running `install.ps1` with `-RelayUrl` ([SETUP.md](SETUP.md#7-optional-live-remote-control)).
  Editing the file by hand works until the next install rewrites it.

**The banner appears, turns grey, and names a reason** — that reason is the fix:
- *"certificate isn't trusted"* → the console's cert is self-signed; set
  `-RelayCertThumbprint` to the value the console prints at startup.
- *"didn't match RelayCertThumbprint"* → the certificate changed (a regenerated PFX makes a
  new thumbprint). Re-copy it.
- *"couldn't reach the console"* → the console isn't running, is still bound to `localhost`,
  or the port is blocked. `Urls` must be `https://0.0.0.0:<port>`, not `localhost`.
- *"rejected this session token"* → the console restarted after you pressed Remote. Press it again.

**"This console doesn't recognise the session"** — same cause: pending sessions are held in
memory only, so a console restart cancels them.

**The picture is live but frozen or black** — capture returns nothing while the workstation is
locked, on the UAC secure desktop, or after an RDP disconnect. It resumes on its own.

**The picture is live but clicks and typing do nothing**
- Check the viewer's **Input: on/off** button — off means view-only.
- Windows refuses synthetic input aimed at an **elevated** window (Task Manager, regedit, an
  admin console) or the **secure desktop** (UAC prompt, lock screen, Ctrl+Alt+Del). Nothing
  can bypass that, by design. The agent logs `SendInput was blocked` at debug level.
- Keyboard goes to the whole page, not the canvas, so you don't need to click first — but the
  browser tab does need focus.

**My own browser shortcuts stopped working** — that's the point: while **Input: on**, this tab
forwards Ctrl+W, F5 and friends to the remote machine instead of acting on itself. Press
**Input: off** to get them back; the session keeps running.

**The session ended while I was still using it** — sessions run in grants of
`RemoteSessionMaxMinutes`. Click **Renew** (it appears in the last five minutes) before the
countdown runs out. After 4 grants renewal is refused by design; start a new session.
`logs\remote-sessions.json` on that machine records `"outcome": "timeout"` for these.

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
Remove-Item C:\Windows\Orch\cache\*.json
Start-Service GitHubOrchestrator
```
This re-installs every active program (checksums re-verified).
