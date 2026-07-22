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

.EXAMPLE
    .\install.ps1 -RepoOwner acme -RepoName orchestrator-repo -Token ghp_xxx
#>
[CmdletBinding()]                                                # enable common parameters (-Verbose, -ErrorAction, ...)
param(
    [Parameter(Mandatory)] [string]$RepoOwner,                  # GitHub owner of the control repo (required)
    [Parameter(Mandatory)] [string]$RepoName,                   # control repo name (required)
    [string]$Token = "",                                        # access token; blank means the repo is public
    [string]$Branch = "",                                       # branch to read (blank -> filled from defaults.json)
    [int]$IntervalMinutes = 0,                                   # sync interval in minutes (0 -> filled from defaults.json)
    [string]$InstallRoot = "",                                  # install folder (blank -> filled from defaults.json)
    [string]$SourceDir = "$PSScriptRoot\publish",               # folder that holds the built exe to copy from
    [string]$DefaultsPath = ""                                  # override path to defaults.json (used when piped in remotely)
)

$ErrorActionPreference = "Stop"                                 # abort on the first error

# --- Load shared defaults (single source of truth) -------------------------------
# defaults.json lives at the repo root next to this scripts/ folder. Every fixed name
# and path (service name, exe name, install root, etc.) is read from it, so you only
# ever change those in one place. -DefaultsPath lets bootstrap.ps1 point us at a copy
# it downloaded, since a piped-in script has no file of its own on disk.
$defaultsFile = if ($DefaultsPath) { $DefaultsPath } else { Join-Path $PSScriptRoot "..\defaults.json" }  # where to read defaults from
if (-not (Test-Path $defaultsFile)) { throw "defaults.json not found at '$defaultsFile'." }               # it must exist
$D = Get-Content $defaultsFile -Raw | ConvertFrom-Json          # parse the shared defaults

$ServiceName = $D.serviceName                                  # the Windows service's internal name (from defaults.json)
$ExeName     = $D.exeName                                       # the executable file name (from defaults.json)
if (-not $InstallRoot)     { $InstallRoot = $D.installRoot }    # fill install folder if the caller didn't set it
if (-not $Branch)          { $Branch = $D.defaultBranch }       # fill branch if not set
if (-not $IntervalMinutes) { $IntervalMinutes = [int]$D.defaultSyncIntervalMinutes }  # fill interval if not set

# --- Elevation check ---
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())  # who is running this
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {                          # not an admin?
    throw "Must run as Administrator."                                                                            # then stop
}

# Make sure the exe we're supposed to install actually exists before doing anything.
if (-not (Test-Path (Join-Path $SourceDir $ExeName))) {
    throw "Published binary not found: $(Join-Path $SourceDir $ExeName). Run: dotnet publish -c Release -r win-x64"
}

Write-Host "Installing to $InstallRoot ..."
New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null  # create the install folder if it isn't there yet

# Stop existing service before overwriting files.
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {  # is the service already installed?
    Write-Host "Stopping existing service..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue  # stop it so its exe isn't locked
    Start-Sleep -Seconds 2                                                # give Windows a moment to release the file
}

Copy-Item -Path (Join-Path $SourceDir '*') -Destination $InstallRoot -Recurse -Force  # copy the built files into the install folder

# --- Write settings ---
$exePath  = Join-Path $InstallRoot $ExeName            # full path to the installed exe
$settings = Join-Path $InstallRoot "appsettings.json"  # full path to the settings file we'll write
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
    New-Service -Name $ServiceName -BinaryPathName "`"$exePath`"" `           # otherwise create the service from scratch
        -DisplayName $D.serviceDisplayName -StartupType Automatic `          # friendly name (from defaults.json) + auto-start at boot
        -Description $D.serviceDescription | Out-Null                        # description (from defaults.json)
}

# Auto-restart on failure.
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null  # if it crashes, restart it (after 60s), up to 3 times

Write-Host "Starting service..."
Start-Service -Name $ServiceName                                            # start it now (this triggers the first sync)
Write-Host "Done. Service '$ServiceName' is running. Logs: $InstallRoot\logs" -ForegroundColor Green  # success message
