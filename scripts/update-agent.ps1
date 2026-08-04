<#
====================================================================================
 FILE PURPOSE (in plain terms):
   Swaps the agent to a newly downloaded build, then CHECKS THE NEW BUILD ACTUALLY
   WORKS — and puts the old one back if it doesn't.

   This exists because auto-update without a way back is the one failure that can't be
   fixed remotely: if a bad build won't start, the agent is gone, and the agent IS how
   you reach the machine. Every machine would need visiting in person. So the update
   runs here, outside the service, keeps a copy of the binary it replaced, and waits
   for evidence the new one is alive before accepting it.

   "Alive" means the service is running AND has completed a fresh sync cycle — not just
   that the process started. A binary that starts and immediately throws would otherwise
   look like a success.

   When it rolls back it records the rejected build's hash in cache\failed-updates.json.
   Without that the agent would see the published hash again on its next sync, install it
   again, crash again, and roll back again, forever.
====================================================================================

.SYNOPSIS
    Installs a staged agent build, verifies it, and rolls back if it fails.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$SourceDir,          # folder holding the staged (verified) new exe
    [string]$InstallRoot = "",                          # install folder (blank -> from defaults.json)
    [string]$Sha256 = "",                               # the build's hash, recorded if it has to be rejected
    [int]$HealthTimeoutSeconds = 300                    # how long to wait for the new build to prove itself
)

$ErrorActionPreference = "Stop"

# --- Locate defaults + install root -----------------------------------------------
$defaultsFile = Join-Path $PSScriptRoot "defaults.json"
if (-not (Test-Path $defaultsFile)) { $defaultsFile = Join-Path $PSScriptRoot "..\defaults.json" }
if (-not (Test-Path $defaultsFile)) { throw "defaults.json not found next to update-agent.ps1." }
$D = Get-Content $defaultsFile -Raw | ConvertFrom-Json

$ServiceName = $D.serviceName
$ExeName     = $D.exeName
if (-not $InstallRoot) { $InstallRoot = $D.installRoot }

$installedExe = Join-Path $InstallRoot $ExeName
$stagedExe    = Join-Path $SourceDir $ExeName
$backupDir    = Join-Path $InstallRoot "backup"
$backupExe    = Join-Path $backupDir $ExeName
$historyFile  = Join-Path $InstallRoot "logs\sync-history.json"
$quarantine   = Join-Path $InstallRoot "cache\failed-updates.json"
$installer    = Join-Path $InstallRoot "install.ps1"

function Write-Step($msg) { Write-Host "[update-agent] $msg" }

if (-not (Test-Path $stagedExe))  { throw "Staged build not found at '$stagedExe'." }
if (-not (Test-Path $installedExe)) { throw "No existing install at '$installedExe'; nothing to update." }
if (-not (Test-Path $installer))  { throw "install.ps1 not found at '$installer'." }

# The newest sync record BEFORE we touch anything. Health means we see one newer than this,
# which proves the new binary got all the way through a cycle rather than merely launching.
function Get-LatestSyncUtc {
    if (-not (Test-Path $historyFile)) { return [datetime]::MinValue }
    try {
        $h = Get-Content $historyFile -Raw | ConvertFrom-Json
        if (-not $h.records -or $h.records.Count -eq 0) { return [datetime]::MinValue }
        return [datetime]::Parse($h.records[-1].timestamp).ToUniversalTime()
    } catch { return [datetime]::MinValue }
}

$syncBefore = Get-LatestSyncUtc
Write-Step "Last sync before update: $syncBefore"

# --- Keep the binary we're replacing ----------------------------------------------
New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
Copy-Item -Path $installedExe -Destination $backupExe -Force
Write-Step "Backed up the current build to $backupExe"

# --- Install the new build (install.ps1 preserves every setting) -------------------
$installFailed = $null
try {
    Write-Step "Installing the staged build..."
    & powershell.exe -ExecutionPolicy Bypass -NoProfile -File $installer -SourceDir $SourceDir -InstallRoot $InstallRoot
    if ($LASTEXITCODE -ne 0) { $installFailed = "install.ps1 exited with code $LASTEXITCODE" }
} catch {
    $installFailed = $_.Exception.Message
}

# --- Did it come up? ---------------------------------------------------------------
$healthy = $false
$reason  = $installFailed
if (-not $installFailed) {
    Write-Step "Waiting up to $HealthTimeoutSeconds s for the new build to complete a sync..."
    $deadline = (Get-Date).AddSeconds($HealthTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 5
        $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if (-not $svc -or $svc.Status -ne 'Running') { $reason = "service is not running"; continue }
        $syncAfter = Get-LatestSyncUtc
        if ($syncAfter -gt $syncBefore) {
            $healthy = $true
            Write-Step "New build completed a sync at $syncAfter - accepted."
            break
        }
        $reason = "service started but no sync completed within $HealthTimeoutSeconds s"
    }
}

if ($healthy) {
    Remove-Item -Path $SourceDir -Recurse -Force -ErrorAction SilentlyContinue   # staged copy no longer needed
    Write-Step "Update complete."
    exit 0
}

# --- Roll back ---------------------------------------------------------------------
Write-Warning "[update-agent] New build rejected ($reason). Restoring the previous build."
try {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    Copy-Item -Path $backupExe -Destination $installedExe -Force
    Start-Service -Name $ServiceName
    Write-Step "Previous build restored and the service restarted."
} catch {
    # This is the genuinely bad case: the new build failed AND the restore failed. Say so as
    # loudly as a script can, because this machine now needs a human.
    Write-Error "[update-agent] ROLLBACK FAILED: $($_.Exception.Message). This machine needs manual attention."
}

# --- Quarantine the bad build so we don't reinstall it on the next sync -------------
if ($Sha256) {
    try {
        New-Item -ItemType Directory -Force -Path (Split-Path $quarantine) | Out-Null
        $q = @{ failed = @() }
        if (Test-Path $quarantine) {
            try { $q = Get-Content $quarantine -Raw | ConvertFrom-Json } catch { $q = @{ failed = @() } }
        }
        $entries = @()
        if ($q.failed) { $entries = @($q.failed) }
        $entries += [pscustomobject]@{
            sha256 = $Sha256.ToLowerInvariant()
            utc    = (Get-Date).ToUniversalTime().ToString("o")
            reason = "$reason"
        }
        if ($entries.Count -gt 20) { $entries = $entries[-20..-1] }   # keep it bounded
        [pscustomobject]@{ failed = $entries } | ConvertTo-Json -Depth 5 | Set-Content -Path $quarantine -Encoding UTF8
        Write-Step "Recorded $Sha256 as a failed build; it won't be retried."
    } catch {
        Write-Warning "[update-agent] Could not write the quarantine file: $($_.Exception.Message). The bad build may be retried."
    }
}

exit 1
