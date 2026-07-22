<#
====================================================================================
 FILE PURPOSE (in plain terms):
   The opposite of install.ps1. It cleanly tears the Orchestrator off a machine:
   stops and deletes the Windows Service, removes the "Orch_*" startup entries it
   left in the registry, and deletes the C:\Windows\Orch folder. Optional switches
   let you keep the files or the startup entries if you want a partial cleanup.
   Run from an Administrator PowerShell window.
====================================================================================

.SYNOPSIS
    Stops and removes the GitHub Orchestrator Windows Service.

.PARAMETER InstallRoot   Install directory (blank = value from defaults.json).
.PARAMETER KeepFiles     Leave files/logs on disk (default: remove them).
.PARAMETER KeepStartup   Leave Orch_* registry startup entries (default: remove them).

.EXAMPLE
    .\uninstall.ps1
#>
[CmdletBinding()]                                        # enable common parameters
param(
    [string]$InstallRoot = "",                          # folder to remove (blank -> filled from defaults.json)
    [switch]$KeepFiles,                                  # if set, leave the files/logs on disk
    [switch]$KeepStartup                                 # if set, leave the Orch_* startup entries alone
)

$ErrorActionPreference = "Stop"                         # stop on the first error

# --- Load shared defaults (single source of truth) ---
$defaultsFile = Join-Path $PSScriptRoot "..\defaults.json"   # repo-root defaults.json
if (-not (Test-Path $defaultsFile)) { throw "defaults.json not found at '$defaultsFile'." }  # it must exist
$D = Get-Content $defaultsFile -Raw | ConvertFrom-Json  # parse the shared defaults
$ServiceName = $D.serviceName                           # the service's internal name (from defaults.json)
if (-not $InstallRoot) { $InstallRoot = $D.installRoot } # fill install folder if the caller didn't pass one

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())  # who is running this
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {                          # not an admin?
    throw "Must run as Administrator."                                                                            # then stop
}

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {   # is the service installed?
    Write-Host "Stopping service..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue  # stop it
    Start-Sleep -Seconds 2                                                # let Windows release the exe
    Write-Host "Deleting service..."
    sc.exe delete $ServiceName | Out-Null                                # remove the service registration
} else {
    Write-Host "Service not installed."                                  # nothing to stop
}

if (-not $KeepStartup) {                                                 # unless the user asked to keep them...
    $runKey = "HKLM:\$($D.registryRunKey)"                              # the registry key holding startup entries (from defaults.json)
    $props = (Get-Item $runKey).Property | Where-Object { $_ -like "$($D.registryEntryPrefix)*" }  # find only OUR entries (prefix from defaults.json)
    foreach ($p in $props) {                                             # for each one we created...
        Write-Host "Removing startup entry $p"
        Remove-ItemProperty -Path $runKey -Name $p -ErrorAction SilentlyContinue  # delete it
    }
}

if (-not $KeepFiles -and (Test-Path $InstallRoot)) {                     # unless keeping files, and if the folder exists...
    Write-Host "Removing $InstallRoot ..."
    Remove-Item -Path $InstallRoot -Recurse -Force -ErrorAction SilentlyContinue  # delete the whole install folder
}

Write-Host "Uninstall complete." -ForegroundColor Green                 # done
