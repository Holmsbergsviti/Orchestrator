# Control Repo (template)

This is the layout for your **private** control repository — the single source of truth the
orchestrator service pulls from. Copy these files into a new private GitHub repo.

```
control-repo/
├─ manifest.json                 # what to install / update / delete
├─ schemas/manifest-schema.json  # validation for manifest.json
└─ programs/                     # your files, versioned
   └─ my-app/
      └─ v1.0/
         └─ my-app.exe
```

## Workflow
1. Add a file under `programs/<name>/<version>/`.
2. `gen-checksum.ps1 -Path <file>` → paste `sha256:...` into `manifest.json`.
3. Set the entry `status: active` (or `deleted` to remove it everywhere).
4. Commit + push. Machines converge within one sync interval.

See the orchestrator repo's [docs/ADDING-PROGRAMS.md](../docs/ADDING-PROGRAMS.md) for the
full field reference.

> Keep this repo **private** and never commit tokens. The service authenticates with a PAT
> configured on each machine, not stored here.
