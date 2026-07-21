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
.PARAMETER Branch       Branch to read (default: main).
.PARAMETER IntervalMinutes  Sync interval (default: 60).
.PARAMETER InstallRoot  Install directory (default: C:\Orchestrator).
.PARAMETER SourceDir    Folder holding published binaries (default: .\publish).

.EXAMPLE
    .\install.ps1 -RepoOwner acme -RepoName orchestrator-repo -Token ghp_xxx
#>
[CmdletBinding()]                                                # enable common parameters (-Verbose, -ErrorAction, ...)
param(
    [Parameter(Mandatory)] [string]$RepoOwner,                  # GitHub owner of the control repo (required)
    [Parameter(Mandatory)] [string]$RepoName,                   # control repo name (required)
    [string]$Token = "",                                        # access token; blank means the repo is public
    [string]$Branch = "main",                                   # branch of the control repo to read
    [int]$IntervalMinutes = 60,                                  # how often the service checks GitHub, in minutes
    [string]$InstallRoot = "C:\Orchestrator",                   # where to install everything
    [string]$SourceDir = "$PSScriptRoot\publish"                # folder that holds the built exe to copy from
)

$ErrorActionPreference = "Stop"                                 # abort on the first error
$ServiceName = "GitHubOrchestrator"                             # the Windows service's internal name
$ExeName     = "orchestrator-service.exe"                       # the executable file name

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
        ManifestPath        = "manifest.json"     # file in the repo that lists the programs
        GitHubToken         = $Token              # token for private repos (blank if public)
        SyncIntervalMinutes = $IntervalMinutes    # minutes between sync cycles
        StartupRegistryKey  = "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"  # registry path for startup entries
        RegistryEntryPrefix = "Orch_"             # prefix so our startup entries are easy to spot/clean up
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
        -DisplayName "GitHub Orchestrator" -StartupType Automatic `          # friendly name + start automatically at boot
        -Description "Syncs and manages programs from a GitHub manifest." | Out-Null
}

# Auto-restart on failure.
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null  # if it crashes, restart it (after 60s), up to 3 times

Write-Host "Starting service..."
Start-Service -Name $ServiceName                                            # start it now (this triggers the first sync)
Write-Host "Done. Service '$ServiceName' is running. Logs: $InstallRoot\logs" -ForegroundColor Green  # success message
