<!--
  FILE PURPOSE (in plain terms): Explains the starter layout for YOUR private control
  repo — the repo the service actually pulls from. It shows the expected folder
  structure and the workflow for adding a program and committing it.
-->
# Control Repo (template)

This is the layout for your **private** control repository — the single source of truth the
orchestrator service pulls from. Copy these files into a new private GitHub repo.

```
control-repo/
├─ manifest.json                 # what to install / update / delete (+ per-machine "target")
├─ fleet.json                    # friendly machine names (written by the operator console)
├─ schemas/manifest-schema.json  # validation for manifest.json
└─ programs/                     # your files, versioned
   └─ my-app/
      └─ v1.0/
         └─ my-app.exe
```

Two branches are in play:
- **`main`** — you (or the console) edit `manifest.json` + `fleet.json` here.
- **`fleet-state`** — the agents auto-create this and commit `state/<machineId>.json`
  heartbeats to it. You don't edit it; the operator console reads it to show the fleet.

## Workflow
1. Add a file under `programs/<name>/<version>/`.
2. `gen-checksum.ps1 -Path <file>` → paste `sha256:...` into `manifest.json`.
3. Set the entry `status: active` (or `deleted` to remove it everywhere). Deleted entries should also include `installPath`, `deletedDate`, and `reason`.
4. Optionally scope it to specific machines with `"target"` (see below). Omit it = all machines.
5. Commit + push. Machines converge within one sync interval.

## Per-machine targeting
Add a `"target"` to any program to limit which machines run it. Omit it (or use `"all"`)
and every machine gets it — including machines that report in later.
```json
"target": "all"                                   // everyone (same as omitting it)
"target": "olegs-laptop"                           // one machine, by hostname
"target": ["olegs-laptop", "DESKTOP-ABC123"]       // a specific set (hostname or machine id)
```
Matching is case-insensitive against each machine's **hostname or its machine id**. Retarget
a program away from a machine and that machine **uninstalls** it on its next sync.

Editing targets by hand works, but the **operator console** (a local web UI) does it for you:
it shows your machines by friendly name and gives you a checkbox grid. See
[docs/CONSOLE.md](../docs/CONSOLE.md).

See the orchestrator repo's [docs/ADDING-PROGRAMS.md](../docs/ADDING-PROGRAMS.md) for the
full field reference.

> Keep this repo **private** and never commit tokens. The service authenticates with a PAT
> configured on each machine, not stored here.
