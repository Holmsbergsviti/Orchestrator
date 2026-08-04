<#
====================================================================================
 FILE PURPOSE (in plain terms):
   Takes the already-built Orchestrator exe and turns it into a proper, always-on
   Windows Service on this machine. It copies the files into place, writes down
   your GitHub settings so the service knows which repo to watch, locks the folder
   so only admins can touch it, then creates and starts the background service.
   Run this from an Administrator PowerShell window.
====================================================================================

.SYNOPSIS
    Installs the GitHub Orchestrator as a Windows Service.

.DESCRIPTION
    Copies the published binaries to the install root, writes GitHub settings
    into appsettings.json, then creates and starts a SYSTEM service.

    Run from an elevated (Administrator) PowerShell prompt.

.PARAMETER RepoOwner    GitHub user or org that owns the repo.
.PARAMETER RepoName     Repository name.
.PARAMETER Token        Personal Access Token (repo:read). Omit for public repos.
.PARAMETER Branch       Branch to read (blank = value from defaults.json).
.PARAMETER IntervalMinutes  Sync interval (0 = value from defaults.json).
.PARAMETER InstallRoot  Install directory (blank = value from defaults.json).
.PARAMETER SourceDir    Folder holding published binaries (default: .\publish).
.PARAMETER DefaultsPath Path to defaults.json; used when the script is piped in remotely.
.PARAMETER RelayUrl     ws(s):// address of the operator console's relay, for live remote control.
                        The console prints the exact value to use when it starts. Leave blank to
                        leave remote control unavailable on this machine.
.PARAMETER RelayCertThumbprint  The console's HTTPS certificate thumbprint (it prints this too).
                        Required only when that certificate is self-signed.

.NOTES
    Settings you don't pass are PRESERVED from the existing appsettings.json, so only a first
    install needs the full argument list. A copy of this script and defaults.json is left in the
    install folder, which means later changes need no download and no repeated arguments.

.EXAMPLE
    # First install: everything has to be specified, because there's nothing to preserve yet.
    .\install.ps1 -RepoOwner acme -RepoName orchestrator-repo -Token ghp_xxx `
        -RelayUrl wss://192.168.1.20:5080 -RelayCertThumbprint A1B2C3...

.EXAMPLE
    # Later: change one setting. Everything else stays as it is.
    powershell -ExecutionPolicy Bypass -File C:\Windows\Orch\install.ps1 -RelayCertThumbprint D4E5F6...

.EXAMPLE
    # Upgrade binaries only, keeping every setting.
    .\install.ps1 -SourceDir .\publish
#>
[CmdletBinding()]                                                # enable common parameters (-Verbose, -ErrorAction, ...)
param(
    # Required for a FIRST install only. On a machine that's already installed, anything you
    # don't pass is kept from the existing appsettings.json, so changing one setting is one
    # parameter rather than the whole list again.
    [string]$RepoOwner = "",                                    # GitHub owner of the control repo
    [string]$RepoName = "",                                     # control repo name
    [string]$Token = "",                                        # access token; blank means the repo is public
    [string]$Branch = "",                                       # branch to read (blank -> filled from defaults.json)
    [int]$IntervalMinutes = 0,                                   # sync interval in minutes (0 -> filled from defaults.json)
    [string]$InstallRoot = "",                                  # install folder (blank -> filled from defaults.json)
    [string]$SourceDir = "$PSScriptRoot\publish",               # folder that holds the built exe to copy from
    [string]$DefaultsPath = "",                                 # override path to defaults.json (used when piped in remotely)
    [switch]$IsWaker,                                           # mark this always-on machine as the Wake-on-LAN sender
    [string]$RelayUrl = "",                                     # console relay address for live remote control (blank = feature off here)
    [string]$RelayCertThumbprint = "",                          # console's cert thumbprint; only needed if it's self-signed
    [bool]$AutoUpdate = $true                                   # keep the agent's own binary current from agent.json
)

$ErrorActionPreference = "Stop"                                 # abort on the first error

# --- Load shared defaults (single source of truth) -------------------------------
# Every fixed name and path (service name, exe name, install root, ...) is read from
# defaults.json, so you only ever change those in one place. Two layouts to support: the repo
# (scripts\install.ps1 with defaults.json one level up) and the install folder, where a copy of
# both sits side by side so reconfiguring later needs no download.
# $PSScriptRoot is empty when this text is run as a scriptblock (how bootstrap.ps1 invokes it),
# which is exactly when -DefaultsPath is supplied instead.
$defaultsFile = $DefaultsPath
if (-not $defaultsFile -and $PSScriptRoot) {
    $localCopy = Join-Path $PSScriptRoot "defaults.json"        # install-folder layout
    $repoCopy  = Join-Path $PSScriptRoot "..\defaults.json"     # repo layout
    if (Test-Path $localCopy) { $defaultsFile = $localCopy } else { $defaultsFile = $repoCopy }
}
if (-not $defaultsFile) { throw "Cannot locate defaults.json; pass -DefaultsPath explicitly." }
if (-not (Test-Path $defaultsFile)) { throw "defaults.json not found at '$defaultsFile'." }               # it must exist
$D = Get-Content $defaultsFile -Raw | ConvertFrom-Json          # parse the shared defaults

$ServiceName = $D.serviceName                                  # the Windows service's internal name (from defaults.json)
$ExeName     = $D.exeName                                       # the executable file name (from defaults.json)
if (-not $InstallRoot)     { $InstallRoot = $D.installRoot }    # fill install folder if the caller didn't set it

# --- Elevation check ---
# Before touching appsettings.json: the install folder is readable only by SYSTEM and
# Administrators, so a non-elevated run would fail to read it and look like a fresh machine
# rather than reporting the real problem.
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())  # who is running this
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {                          # not an admin?
    throw "Must run as Administrator."                                                                            # then stop
}

# --- Merge with the settings already on this machine ------------------------------
# This file is rewritten from scratch every run, so anything NOT carried over here is
# silently reset to a default. That's how RelayUrl used to disappear on every upgrade.
# So: an explicitly-passed parameter wins, otherwise keep what's already installed,
# otherwise fall back to defaults.json.
$settingsPath = Join-Path $InstallRoot "appsettings.json"
$old = $null
if (Test-Path $settingsPath) {
    try { $old = (Get-Content $settingsPath -Raw | ConvertFrom-Json).Orchestrator }
    catch { Write-Warning "Existing appsettings.json is unreadable; treating this as a fresh install." }
}

# $PSBoundParameters is per-scope, so snapshot the SCRIPT's copy here rather than reading it
# from inside a helper function, where it would describe that function's arguments instead.
# Checking it (not emptiness) is what distinguishes "left it out" from "deliberately set it
# to blank" — clearing RelayUrl to switch remote control off has to remain possible.
$given = $PSBoundParameters

$RepoOwner           = if ($given.ContainsKey('RepoOwner'))           { $RepoOwner }           elseif ($old -and $old.RepoOwner)           { $old.RepoOwner }           else { "" }
$RepoName            = if ($given.ContainsKey('RepoName'))            { $RepoName }            elseif ($old -and $old.RepoName)            { $old.RepoName }            else { "" }
$Token               = if ($given.ContainsKey('Token'))               { $Token }               elseif ($old -and $old.GitHubToken)         { $old.GitHubToken }         else { "" }
$Branch              = if ($given.ContainsKey('Branch'))              { $Branch }              elseif ($old -and $old.Branch)              { $old.Branch }              else { $D.defaultBranch }
$IntervalMinutes     = if ($given.ContainsKey('IntervalMinutes'))     { $IntervalMinutes }     elseif ($old -and $old.SyncIntervalMinutes)  { [int]$old.SyncIntervalMinutes } else { [int]$D.defaultSyncIntervalMinutes }
$RelayUrl            = if ($given.ContainsKey('RelayUrl'))            { $RelayUrl }            elseif ($old -and $old.RelayUrl)            { $old.RelayUrl }            else { "" }
$RelayCertThumbprint = if ($given.ContainsKey('RelayCertThumbprint')) { $RelayCertThumbprint } elseif ($old -and $old.RelayCertThumbprint) { $old.RelayCertThumbprint } else { "" }
$IsWakerValue        = if ($given.ContainsKey('IsWaker'))             { [bool]$IsWaker }       elseif ($old)                               { [bool]$old.IsWaker }       else { $false }
$AutoUpdateValue     = if ($given.ContainsKey('AutoUpdate'))          { [bool]$AutoUpdate }    elseif ($old -and $null -ne $old.AutoUpdate) { [bool]$old.AutoUpdate }   else { $true }

if (-not $RepoOwner -or -not $RepoName) {
    throw "No existing install found at '$InstallRoot', so -RepoOwner and -RepoName are required for a first install."
}

# Are there binaries to install? If not, and this machine already has them, treat the run as a
# settings-only reconfigure — that's what makes "change one setting" a short command instead of
# a 36 MB download. Without an existing install there's nothing to fall back on, so it's fatal.
$haveBinaries = Test-Path (Join-Path $SourceDir $ExeName)
$alreadyInstalled = Test-Path (Join-Path $InstallRoot $ExeName)
if (-not $haveBinaries) {
    if (-not $alreadyInstalled) {
        throw "Published binary not found: $(Join-Path $SourceDir $ExeName). Run: dotnet publish -c Release -r win-x64"
    }
    Write-Host "No new binaries supplied - reconfiguring the existing install." -ForegroundColor Cyan
}

Write-Host "Installing to $InstallRoot ..."
New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null  # create the install folder if it isn't there yet

# Stop existing service before overwriting files.
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {  # is the service already installed?
    Write-Host "Stopping existing service..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue  # stop it so its exe isn't locked
    Start-Sleep -Seconds 2                                                # give Windows a moment to release the file
}

if ($haveBinaries) {
    Copy-Item -Path (Join-Path $SourceDir '*') -Destination $InstallRoot -Recurse -Force  # copy the built files into the install folder
}

# Leave this script and defaults.json beside the install, so reconfiguring later needs nothing
# downloaded and no arguments beyond the one being changed. Skipped when they're already the
# same file — which is exactly the case when this IS the local copy being re-run.
$installedScript   = Join-Path $InstallRoot "install.ps1"
$installedDefaults = Join-Path $InstallRoot "defaults.json"
if ($PSCommandPath -and $PSCommandPath -ne $installedScript) {
    Copy-Item -Path $PSCommandPath -Destination $installedScript -Force -ErrorAction SilentlyContinue
}
# update-agent.ps1 too: self-update runs it to supervise the swap and roll back a bad build.
# Without it on disk the agent refuses to auto-update rather than updating unsupervised.
if ($PSCommandPath) {
    $updaterSource = Join-Path (Split-Path $PSCommandPath) "update-agent.ps1"
    $updaterDest   = Join-Path $InstallRoot "update-agent.ps1"
    # Skip when they're the same file — that's the case when self-update re-runs the local copy.
    if ((Test-Path $updaterSource) -and ((Resolve-Path $updaterSource).Path -ne $updaterDest)) {
        Copy-Item -Path $updaterSource -Destination $updaterDest -Force -ErrorAction SilentlyContinue
    }
}
if ((Resolve-Path $defaultsFile).Path -ne $installedDefaults) {
    Copy-Item -Path $defaultsFile -Destination $installedDefaults -Force -ErrorAction SilentlyContinue
}

# --- Write settings ---
$exePath  = Join-Path $InstallRoot $ExeName            # full path to the installed exe
$settings = $settingsPath                              # full path to the settings file we'll write
# Build the settings object (ordered so the JSON keys come out in a predictable order).
$config = [ordered]@{
    Orchestrator = [ordered]@{
        RootPath            = $InstallRoot        # base folder the service works out of
        RepoOwner           = $RepoOwner          # GitHub owner to read from
        RepoName            = $RepoName           # GitHub repo to read from
        Branch              = $Branch             # branch to read
        ManifestPath        = $D.manifestFileName # file in the repo that lists the programs (from defaults.json)
        GitHubToken         = $Token              # token for private repos (blank if public)
        SyncIntervalMinutes = $IntervalMinutes    # minutes between sync cycles
        StartupRegistryKey  = $D.registryRunKey   # registry path for startup entries (from defaults.json)
        RegistryEntryPrefix = $D.registryEntryPrefix  # prefix so our startup entries are easy to spot/clean up (from defaults.json)
        IsWaker             = $IsWakerValue        # true = this machine sends Wake-on-LAN packets for the fleet
        AutoUpdate          = $AutoUpdateValue     # true = install the agent build published in the control repo's agent.json
        # Live remote control. This file is rewritten from scratch on every install, so anything
        # missing here is silently reset to its built-in default — which is exactly how a
        # hand-edited RelayUrl would disappear on the next upgrade. Pass it as a parameter instead.
        RelayUrl            = $RelayUrl            # console relay address; blank = remote control unavailable here
        RelayCertThumbprint = $RelayCertThumbprint # pin for a self-signed console certificate; blank = require normal CA trust
    }
}
$config | ConvertTo-Json -Depth 5 | Set-Content -Path $settings -Encoding UTF8  # turn it into JSON text and save it

# Restrict directory permissions: SYSTEM + Administrators only.
icacls $InstallRoot /inheritance:r /grant:r "SYSTEM:(OI)(CI)F" "Administrators:(OI)(CI)F" | Out-Null  # lock the folder down so normal users can't tamper

# --- Create / update service ---
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {   # service already exists?
    Write-Host "Updating service binary path..."
    sc.exe config $ServiceName binPath= "`"$exePath`"" start= auto | Out-Null  # just repoint it at the (new) exe and set auto-start
} else {
    Write-Host "Creating service..."
    # Create the service from scratch. Display name + description come from defaults.json;
    # StartupType Automatic makes it start at boot. (A line-continuation backtick must be
    # the LAST character on its line, so these comments stay above the command, not inline.)
    New-Service -Name $ServiceName -BinaryPathName "`"$exePath`"" `
        -DisplayName $D.serviceDisplayName -StartupType Automatic `
        -Description $D.serviceDescription | Out-Null
}

# Auto-restart on failure.
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null  # if it crashes, restart it (after 60s), up to 3 times

Write-Host "Starting service..."
Start-Service -Name $ServiceName                                            # start it now (this triggers the first sync)
Write-Host "Done. Service '$ServiceName' is running. Logs: $InstallRoot\logs" -ForegroundColor Green  # success message

# Say plainly whether remote control will work here — otherwise the first sign of trouble is a
# viewer window that never shows a picture, with nothing on this machine explaining why.
if ($RelayUrl) {
    Write-Host "Live remote control: enabled (relay $RelayUrl)." -ForegroundColor Green
} else {
    Write-Host "Live remote control: NOT configured. Re-run with -RelayUrl <the address the console prints> to enable it." -ForegroundColor Yellow
}
