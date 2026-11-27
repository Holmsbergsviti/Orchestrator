# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
dotnet build Orchestrator.sln          # build both projects (agent + console)
dotnet test                            # run all unit tests (xUnit)
dotnet test --filter FullyQualifiedName~TargetingTests            # one test class
dotnet test --filter FullyQualifiedName~ManifestServiceTests.NewProgram_IsPlannedAsInstall  # one test

# Run the operator console locally (serves http://localhost:5080):
dotnet run --project src/Orchestrator.Console -- /path/to/control-repo-clone

# Run the agent in console mode on Windows (NOT as a service; bare invocation self-installs):
dotnet run --project src/Orchestrator.Service -- run
```

- **The .NET 8 SDK on this machine is user-local at `~/.dotnet` and not on PATH.** Prefix commands
  with `export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"` (or call `~/.dotnet/dotnet`).
- Both projects target **net8.0** but the service is **net8.0-windows** (it uses the registry, WMI,
  scheduled tasks). It still *compiles and unit-tests* on macOS/Linux; only the Windows *runtime*
  paths (registry, `shutdown.exe`, WMI, WoL, launching processes) can't be exercised off Windows.
- No linter is configured. Build warnings are treated as the quality bar (builds are `-warnaserror`-clean).
- Publish the self-contained agent exe: `scripts/publish.ps1`. One-command fleet install: `scripts/bootstrap.ps1`
  (downloads or `-BuildFromSource` builds, then runs `scripts/install.ps1`).

## Architecture: GitHub is the (two-way) control plane

There is **no server**. Everything coordinates through a private **control repo** on GitHub, which is
separate from this code repo:

- **`main` branch** (operator-owned): `manifest.json` (programs + targeting), `fleet.json` (friendly
  machine labels), `commands.json` (shutdown/restart/wake), and the actual program files under `programs/`.
- **`fleet-state` branch** (agent-owned, auto-created): `state/<machineId>.json` heartbeats each agent
  commits (hostname, OS, last-seen, applied program ids, **MAC addresses** for Wake-on-LAN).

Two executables talk only through those files:

- **`src/Orchestrator.Service`** — the **agent**. A Windows service (runs as SYSTEM) that every
  `SyncIntervalMinutes` pulls the manifest, installs/updates/removes programs, executes commands, and
  reports a heartbeat. Reads via the GitHub Contents API (works on private repos with a PAT); writes
  heartbeats via the API PUT path (**needs a write-scoped token**; degrades to read-only + a warning).
- **`src/Orchestrator.Console`** — the **operator console**. A cross-platform ASP.NET app you run on
  your own machine. It drives a **local git clone** of the control repo by shelling out to `git`
  (`GitRepo.cs`) — reads manifest/fleet.json from `origin/main` and heartbeats from `origin/fleet-state`,
  and on save **fast-forwards main, edits, commits, and pushes**. It never uses a PAT; it uses your
  existing git credentials. Agent changes need a reinstall to take effect; console changes are live on
  a rebuild/restart (the page HTML is served from source `wwwroot/`).

### Single source of truth: `defaults.json`
Fixed names/paths (install root, service name, exe name, branch names, intervals) live **only** in
repo-root `defaults.json`. The service embeds it at build time and reads it via `OrchestratorDefaults.cs`;
the exe's `<AssemblyName>` is derived from `defaults.json`'s `exeName` in the csproj; the PowerShell
scripts read it at runtime. Change a value there, rebuild, and it flows everywhere.

## Agent control flow (the parts that span files)

`Worker` loops → `SyncService.RunSyncAsync` is the orchestrator of one cycle:

1. `GitHubClient.GetManifestAsync` fetches the manifest.
2. **`ManifestService.FilterForMachine`** reduces it to *this machine's* view: active programs whose
   `target` doesn't match this machine's hostname/id are turned into `deleted` entries so they get
   **uninstalled locally**. This is how per-machine targeting works — the rest of the pipeline is unchanged.
3. `ManifestService.BuildPlan` diffs the effective manifest against the local baseline cache
   (`cache/local-manifest.json`) + on-disk state to produce install/update/delete/up-to-date actions.
   A version bump *or* a checksum mismatch triggers reinstall.
4. Actions run; then run-now requests, admin commands, and a heartbeat (`FleetReporter`).

**Startup launching is gated (important).** Startup entries do NOT point at the program. `StartupManager`
(→ `RegistryService` for HKLM Run, or `ScheduledTaskService` for `runAsAdmin`) registers
`orchestrator-service.exe run-program <id>`. At logon that lands in `ProgramLauncher` (the `run-program`
verb in `Program.cs`, sharing DI with the host via `ServiceRegistration.cs`), which **re-checks the
current manifest and only launches the program if it's still active + targeted** — so a just-deleted
program never runs at boot, and it launches in the interactive user session.

**Session matters.** The service runs in Windows **session 0** (no interactive desktop/audio):
- `runOnceInstalled` runs there (fine for silent tasks, invisible for UI/audio).
- **Screenshots and live remote sessions are started by the service itself** (`UserSessionLauncher`): it copies its
  SYSTEM token, moves it to the signed-in user's session (`TokenSessionId`) and `CreateProcessAsUser`s the helper on
  `winsta0\default`, with SYSTEM's own environment (never the user's: the self-extracting exe would unpack into a
  user-writable temp folder). Reason: the install folder is locked to SYSTEM+Administrators (it holds the token), so
  the old scheduled task "run as the user" failed with 0x80070005 for any account that isn't allowed to run the
  agent. There is no fallback to a scheduled task any more: a failed launch is logged and the next request tries again. Nobody signed in (login screen, e.g. after a restart) is
  covered too: the helper is started in the console session on `winsta0\winlogon`, and `InputDesktop.Attach()`
  (called per frame by `ScreenCaptureService` and per event by `RemoteInputInjector`) moves the calling thread to
  whichever desktop is on screen, so the lock screen and UAC prompts work as well. `SessionBanner` owns a window,
  which can't change desktops, so a 1 s timer destroys and recreates it on the new desktop (`_moving` stops that
  from ending the session). Ctrl+Alt+Del can't be injected (would need `SendSAS`).
- `runRequest` ("Run now") and startup runs use a **one-time interactive scheduled task**
  (`ScheduledTaskService.RunInteractiveOnce`, InteractiveToken) so they run in the logged-in user's
  session — that's the only way GUI/audio programs are visible.

**Nonce pattern for one-shot actions.** `runRequest` (run-now), `commands` (shutdown/restart), and
`wake` all carry a nonce/id; the agent records executed ids in `config.json` (`CompletedRunRequests`,
`CompletedCommands`, `CompletedWakes`) so each fires exactly once and never loops after reboot.

**Wake-on-LAN.** The agent can't power on an off machine, so a machine installed with `IsWaker=true`
sends WoL magic packets (`WakeSender`) for `wake` requests to the target MACs reported in heartbeats.
Only works within a LAN segment (broadcast) — one waker per broadcast domain.

**Live remote control is the one feature that does NOT flow through git.** A screen can't stream
over commit polling, so the console hosts a WebSocket relay (`RelayHub`) and the agent dials
*out* to it (`RemoteSessionService` → `Orchestrator:RelayUrl`); git only carries the session
nonce (`commands.json.remoteSessions`, same pattern as screenshots). Consequences to keep in mind:
- **Reaching PCs on other networks: `TunnelService`** (`Console:Tunnel`) runs `cloudflared` as a child, reads the
  `*.trycloudflare.com` name from its output (only once it logs "Registered tunnel connection"), and
  `ControlRepo` advertises that (no thumbprint — Cloudflare's own certificate) instead of the LAN name while it is
  up. The tunnel targets a **separate loopback listener** (`Console:TunnelPort`), and a guard middleware that runs
  before auth lets that port reach `/relay/agent` and nothing else: via cloudflared every request looks local, and a
  console without an `AccessToken` would otherwise serve the whole UI to the internet. Keep that guard first.
  Sessions through the tunnel are **sealed end to end** (`RelayCipher`, AES-256-GCM, per-direction HKDF keys, counter
  per message): the console makes a per-session key, ships it in the signed hint (`relayKey`, GitHub — never the
  tunnel), registers it with `RelayHub`, and `RelayEncryptedPump` opens/seals at the console edge (the browser leg is
  localhost). A session with a key accepts only sealed messages; there is no plaintext fall-back. `RelayCipher` exists
  in both projects and a test seals with one and opens with the other — keep them in step. LAN sessions carry no key.
- **Where the agent dials comes from a signed, throwaway hint** (`relay-hint` branch: one parentless
  commit with `relay-hint.json` + `.sig`, force-pushed by `ControlRepo.PublishRelayHintAsync` before the
  session request lands on main and deleted by `RelayHintJanitor` once `RelayHub` no longer knows the
  session). The console advertises its *name* (`RelayAdvert`), not an IP. Only agents that enforce signing
  honour it (`SyncService`) — otherwise a copied token could redirect a PC's screen; they fall back to
  `Orchestrator:RelayUrl`. Values land on a command line, so `RelayHint` validates them strictly; the
  address is kept out of agent logs. Old/non-signing agents still need the manual setting below.
- **`RelayUrl` must be configured per machine on agents that don't use the hint, or the feature is silently unavailable there.**
  `install.ps1` rewrites `appsettings.json` wholesale, so any new agent setting must be added
  as an installer parameter too, or it resets to its default on the next install.
- Pending sessions live in `RelayHub`'s memory — restarting the console cancels them.
- Non-localhost `Urls` forces `Console:AccessToken` + a cert (fail-fast in `Program.cs`). Agents
  pin a self-signed cert by SHA-1 thumbprint (`RelayCertThumbprint`); the console prints it at startup.
- Failures must stay visible on both ends: the target's `SessionBanner` shows the reason (only when `ShowSessionBanner` is on, default off since 2026-11-26 at the owner's request: his PCs are unshared; otherwise it goes to the agent log), and
  `RelayHub` sends the viewer JSON status text messages (frames are binary, so they never collide).
- Input flows browser→agent as JSON **text** on the same socket (`RemoteInputEvent` parses it,
  `RemoteInputInjector` injects via `SendInput`). Mouse positions are fractions of the virtual
  desktop, never pixels — the capture is downscaled and may span monitors. Parsing is
  deliberately strict (it becomes real keystrokes), and every held key/button is released when
  a session ends, or it stays stuck down on that desktop.
- **No automatic clipboard sync between operator and remote machine** — Ctrl+C/Ctrl+V on the
  canvas are just forwarded keystrokes, so they only ever act on whichever clipboard the remote
  machine's own OS is holding. The viewer's **Clipboard** panel is the explicit substitute: `paste`
  (carries free-form `text`, length-capped not character-filtered, unlike `code`) sets the remote
  clipboard (`RemoteInputInjector.SetClipboardAndPaste`, Win32 `OpenClipboard`/`SetClipboardData`)
  and immediately sends Ctrl+V; `clipboard-request` reads the remote clipboard
  (`GetClipboardText`) and the reply crosses back to the viewer as a `{"t":"clipboard","text":...}`
  JSON message. That reply can only go out through the frame loop (the single socket writer), so
  the receive loop hands it over the same flag-plus-field pattern as the deadline broadcast
  (`_clipboardResponsePending`/`_clipboardResponseText`) rather than writing to the socket itself.
- Sessions run in renewable **grants** (`RemoteSessionMaxMinutes`, ceiling `MaxSessionGrants`).
  The agent owns the deadline and broadcasts it; the viewer only displays it. Only the frame
  loop writes to the agent's socket — the receive loop mutates state and flags it, because a
  WebSocket allows one concurrent writer. `RunInteractiveRemoteSessionOnce` gives the helper a kill time of
  `RemoteSessionAbsoluteMax` + 15 min so a legitimately renewed session isn't cut short.
- Sessions are audited to `logs/remote-sessions.json` — its own file, not `sync-history.json`,
  because the session process (user session) and the service (session 0) would otherwise
  read-modify-write the same file and corrupt it.

**Diagnosing a machine that drops offline intermittently (`SecurityInventory`, `LanRelayService`).**
Heartbeats carry Windows Firewall's on/off state per profile (`FirewallProfiles`) alongside the
existing antivirus list; separately, the agent re-logs its FULL installed-programs list (registry
Uninstall keys, broader than Security Center — catches firewalls, VPN/proxy clients and other
software that never registers as antivirus) every ~6 hours, so it's within reach of the log tail's
400-line window whenever the next upload succeeds. Both exist because "what's blocking this PC"
is usually NOT a registered antivirus product, especially on second-hand machines.
- **LAN relay.** If a machine fails to reach GitHub for `LanRelayFailureThreshold` heartbeats in a
  row (default 5), it UDP-broadcasts a `relay-request` on `lanRelayPort` (defaults.json, 51900,
  fixed fleet-wide — not a per-machine setting) and serves its heartbeat bytes over a short-lived
  TCP connection to whichever peer answers; that peer (`LanRelayListenerHost`, running for the
  whole service lifetime, separate from `Worker`'s sync loop) writes them to
  `state/<machineId>.json` on the peer's behalf, unchanged. Fire-and-forget: a pickup resets the
  failure counter so it doesn't broadcast every single cycle once a peer is helping, but there's no
  confirmation the write actually landed — if it silently didn't, the streak just builds back up.
  One broadcast domain only, same limit as Wake-on-LAN.
- **Deliberately LAN-trust only, with one hard boundary.** Unlike signed control files or
  `RelayCipher` sessions, a relay request carries no per-machine authentication — anything on the
  same network segment can send one, same trust model as the existing WakeSender broadcast. What
  keeps that bounded: `LanRelayPayload.TryDecode` refuses any path that isn't EXACTLY
  `state/<thatMachineId>.json`, so even a forged request can only ever result in a bogus heartbeat
  for some machine id — never a write to `manifest.json`, `commands.json`, another machine's
  screenshots, or anywhere else in the repo. `-LanRelayEnabled $false` at install time turns off
  both roles (asking and answering) on one machine.

**The agent updates itself (`SelfUpdateService`).** `.github/workflows/publish-agent.yml` builds
on every push to `main`, force-pushes the exe to the `dist` branch (orphan commit — a 36 MB
binary per push would bloat the repo), and writes `agent.json` to the **control** repo. Points
that matter:
- The binary is public but its SHA-256 lives in the **private** control repo, so publishing code
  to the fleet needs both repos. A build that doesn't match is refused, not retried.
- Identity is the hash of the *running exe*, not a version string — a machine that was rolled
  back or interrupted mid-update still converges.
- A service can't overwrite its own binary, so the swap is a one-time **SYSTEM** scheduled task
  running `install.ps1` (which preserves settings). The update check runs last in the cycle,
  after the heartbeat, because it can stop the service.
- Brakes: `enabled:false` in `agent.json` (fleet-wide) and `AutoUpdate:false` (per machine).
- The swap is supervised by `update-agent.ps1`, not `install.ps1` directly: it backs up the
  current exe, installs, waits for a *new sync-history record* (proof the build works, not just
  that it launched), and restores the backup otherwise. A rejected hash goes in
  `cache/failed-updates.json`, which `SelfUpdateService` honours — without that, a rollback puts
  the machine back on the old build, the published hash still mismatches, and it reinstalls the
  broken build forever.
- Self-update downloads only the exe, yet runs the `install.ps1` already on disk — which
  rewrites `appsettings.json` keeping only the keys it knows. So both scripts are embedded in
  the exe and `InstalledScripts` rewrites the on-disk copies at service start; a new setting
  added to `install.ps1` reaches machines with the next build instead of being wiped by it.
- **Signed control files.** `manifest.json`, `commands.json` and `agent.json` each have a
  `.sig` beside them, made by the operator's key (console: `ControlSigning`; CI: openssl in
  `publish-agent.yml`). A build compiled with `-p:OrchestratorSigningKey=<public key>` (CI does
  this from the `AGENT_SIGNING_KEY` secret) refuses those files unsigned, edited, or older than
  the last one accepted (`ControlFileVerifier`), and refuses programs without a checksum. A
  build without the key enforces nothing. The console refuses to save on top of an unsigned
  change, since saving signs the whole file. Keep the three copies of the signed format in step.
- Heartbeats carry `agentSha256` (the running exe's hash) and the console compares it to
  `agent.json`, so "which machines are on the current build" is answerable.
- **Encrypted fleet data.** Signing stops a stolen token from *commanding* the fleet; it does
  nothing about *reading* it. Separately, heartbeats (`state/<id>.json`), uploaded log tails
  (`logs/<id>.txt`) and screenshots (`screenshots/<id>/*.jpg`) are sealed to the operator's
  **data** key before an agent ever writes them to GitHub (`FleetDataCipher`: a per-message
  ephemeral-EC-Diffie-Hellman + HKDF + AES-256-GCM scheme, same shape as `RelayCipher` but with
  one standing keypair instead of a per-session one). `DataEncryptionKeys` on the console holds
  the private half (made with the `data-key` verb, stored like the signing key but in a
  separate file — one key, one job, so neither use can weaken the other); `FleetDataEncryption`
  in the agent holds only the baked-in public half (`-p:OrchestratorDataPublicKey=<SPKI
  base64>`, from the `AGENT_DATA_PUBLIC_KEY` repo **variable** — it's public, so unlike
  `AGENT_SIGNING_KEY` it isn't a secret). A build without the key sends plain data, same as
  before; the console's `DecryptIfNeeded` recognizes which is which by a magic prefix, so old
  and new agents coexist on one fleet with no flag day. Losing the data key only means old
  uploads made to it are unrecoverable — nothing else in the fleet depends on it, unlike losing
  the signing key.

**The exe is `WinExe`, not `Exe`.** Four of its five modes run with no user watching, and a
console subsystem gave the screenshot/remote-session verbs a visible terminal window in the
user's session that closing would kill. Verbs a human runs (`run`, `install`) call
`AttachConsole(ATTACH_PARENT_PROCESS)` to print into the calling terminal.

## Console specifics

- Manifest edits go through a **JSON DOM** (`System.Text.Json.Nodes`), not deserialize/reserialize, so
  only the intended fields change and everything else round-trips untouched.
- Targeting is written as **hostnames** (readable), translated from the stable machine ids the UI tracks.
- Saves refuse a **dirty or diverged** clone (fast-forward-only) to avoid clobbering; surfaced as a UI error.
- A save that fails at any step is **rolled back** (`GitRepo.CommitAndPushAsync` resets to the pre-edit HEAD and
  removes files it created). Otherwise a rejected push left a commit stranded on local main and every later save was
  refused as diverged — easy to hit, because CI pushes `agent.json` to the control repo on every agent build. A
  rejected push (`PushRejectedException`) is retried once from a fresh fetch, and all writes run under one gate
  (`ControlRepo.MutateAsync`). New write operations must go through `MutateAsync` and `CommitSignedAsync`.

- **The console updates itself (`ConsoleUpdater`).** It finds the code clone it runs from (walks up to
  the `.git` + `Orchestrator.sln` folder, or `Console:CodeRepoPath`), reports commits behind `origin/main`,
  and on request fast-forwards — refusing a dirty tree or local commits not on GitHub, never merging. The page
  shows a red bar + blocking overlay when behind (held back while there are unsaved edits; after a failed update
  an escape hatch is offered so a broken clone can't lock the operator out). "Restart needed" is computed from
  the commit the *process started at* vs HEAD, so it survives page reloads and manual `git pull`s.

- **Activity log (`ActivityLog`, `GET /api/activity`).** Not stored anywhere: it is the "console: ..." commit
  subjects on `main` and `fleet-state`, read back with `git log`. So **a new console action needs a commit subject
  `ActivityLog.Parse` understands** (add a case and a test), or it silently won't show up. Housekeeping commits
  (relay hint, signing) are deliberately skipped.

- **Agent log upload.** Every `LogUploadMinutes` (default 2, checked once per sync, independent of the heartbeat's 30-minute cap) `FleetReporter` commits the tail of the
  newest `logs\log-*.txt` to `logs/<machineId>.txt` on `fleet-state` (`UploadLogs`, default on); the console serves it at
  `GET /api/log/{machineId}` (the "Log" link). Forget removes it too.
  **On-PC log retention (`LogRetention`).** The agent's own `logs\log-*.txt` are trimmed at startup and every ~6 h by age
  (`LogRetentionDays`, default 1 = about the past day) then by total size (`LogMaxTotalMegabytes`, default 5), oldest first; the newest file
  and the json files are never touched. Serilog's `retainedFileCountLimit` follows the same days. `System.Net.Http.HttpClient`
  is raised to Warning because its 4 lines per request were most of the volume.

- **Removing a machine.** "Forget" (`ControlRepo.ForgetMachineAsync`) deletes `state/<id>.json` and
  `screenshots/<id>` from the `fleet-state` branch via a scratch clone (`GitRepo.RemovePathsOnRemoteBranchAsync`;
  the branch is never checked out here, and a lost push race restarts from a fresh clone). "Uninstall" is a
  `commands.json` action that expires in 1 hour on purpose (a reinstalled machine forgets which commands it ran,
  so a long-lived one could remove its fresh agent). The agent handles it in `SyncService.UninstallAgentAsync`:
  schedule `uninstall-agent.ps1` (embedded, extracted to temp, so it can delete its own folder) as a SYSTEM task,
  *then* delete its own heartbeat, then skip heartbeat/self-update for the rest of the cycle.

## Conventions

- Every source file opens with a plain-language "FILE PURPOSE" comment block, and lines carry inline
  comments. Match that density when editing.
- Cross-platform code guards Windows-only calls with `OperatingSystem.IsWindows()` and
  `[SupportedOSPlatform("windows")]`; keep new Windows APIs behind those guards so the console/tests build.
- `docs/` (SETUP, ADDING-PROGRAMS, CONSOLE, TESTING, TROUBLESHOOTING) and `repo-template/` (the starter
  control repo) are user-facing — update them when you change manifest fields or console behavior.
