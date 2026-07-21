<!--
  FILE PURPOSE (in plain terms): The day-to-day reference for managing programs. It
  shows how to add, update, and remove a program by editing manifest.json, and gives
  a full field-by-field reference for what each manifest option means.
-->
# Adding, Updating & Removing Programs

All changes are commits to your **control repo**. Orchestrators converge within one sync
interval (default 60 min).

## Add a program
1. Drop the file in a versioned folder:
   ```
   programs/my-app/v1.0/my-app.exe
   ```
2. Generate its checksum:
   ```powershell
   .\scripts\gen-checksum.ps1 -Path programs/my-app/v1.0/my-app.exe
   ```
3. Add an entry to `manifest.json`:
   ```json
   {
     "id": "my-app-001",
     "name": "my-app",
     "version": "1.0",
     "status": "active",
     "type": "exe",
     "path": "programs/my-app/v1.0/my-app.exe",
     "checksum": "sha256:<hash>",
     "installPath": "C:\\Orchestrator\\programs\\my-app",
     "fileName": "my-app.exe",
     "arguments": "--silent",
     "runAtStartup": true,
     "runAsAdmin": false,
     "runOnce": false
   }
   ```
4. Commit + push.

### `path` vs `url`
Prefer **`path`** (repo-relative) — the service resolves it through the GitHub Contents
API, which works for private repos. `url` (a `raw.githubusercontent.com` link) also works;
the service parses the repo path out of it.

## Update a version
1. Add `programs/my-app/v1.1/my-app.exe`.
2. Change `version` → `1.1`, update `path` and `checksum`.
3. Commit. Machines detect the version change and replace the file.

> The diff triggers on **`version` change** (or a missing/corrupt local file). Bump the
> version whenever the file changes, and always update the checksum to match.

## Remove a program from all machines
Change its status and (optionally) note why:
```json
{ "id": "my-app-001", "name": "my-app", "version": "1.1", "status": "deleted",
   "installPath": "C:\\Orchestrator\\programs\\my-app",
   "deletedDate": "2026-07-20T00:00:00Z",
   "reason": "retired" }
```
Next sync deletes the install folder and the `Orch_my-app` startup entry (Run value or
Scheduled Task). Prefer `status: deleted` and keep the entry in the manifest at least one
cycle so every machine sees the `deleted` state before you drop it entirely.

Deleting the entry outright also works: a program that was installed and then disappears
from the manifest completely is uninstalled on the next sync, using its last-known local
`installPath`. The `status: deleted` route is still preferred because it's explicit and
carries a `reason` into the logs.

## Field reference
| Field | Required | Notes |
|-------|----------|-------|
| `id` | yes | Stable unique key; diffing is by `id`. |
| `name` | yes | Used for the startup entry name (`Orch_<name>` Run value or Scheduled Task). |
| `version` | yes | Change it to trigger an update. |
| `status` | yes | `active` or `deleted`. |
| `type` | active | `exe` `batch` `powershell` `vbs` `python`. |
| `path` / `url` | active | One required. `path` preferred. |
| `checksum` | recommended | `sha256:<64hex>`. Omitted = installs unverified (logged warn). |
| `installPath` | active | Local dir; also used to locate files for deletion. |
| `fileName` | active | File name written into `installPath`. |
| `arguments` | no | Appended to the startup command. |
| `runAtStartup` | no | Registers the program to launch at startup (mechanism depends on `runAsAdmin`). |
| `runAsAdmin` | no | `false` → `HKLM\...\Run` entry (interactive user, non-elevated). `true` → Scheduled Task running as **SYSTEM** with highest privilege, at boot. |
| `runOnce` | no | Executes once per machine right after install. |
| `deletedDate` | deleted | Timestamp for `status: deleted` entries. |
| `reason` | deleted | Brief explanation for the removal. |

## Startup command by type
| type | Run entry written |
|------|-------------------|
| exe | `"<file>" <args>` |
| powershell | `powershell.exe -ExecutionPolicy Bypass -NoProfile -WindowStyle Hidden -File "<file>" <args>` |
| batch | `cmd.exe /c "<file>" <args>` |
| vbs | `wscript.exe "<file>" <args>` |
| python | `pythonw "<file>" <args>` (Python must be on PATH) |
