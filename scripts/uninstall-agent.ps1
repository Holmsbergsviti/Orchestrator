<#
====================================================================================
 FILE PURPOSE (in plain terms):
   Removes the Orchestrator agent from a PC: stops and deletes the Windows service, removes
   the scheduled tasks and startup entries the agent made, and deletes its install folder.

   The agent runs this itself, as a one-time SYSTEM task, when the operator presses
   "Remove -> Uninstall agent" in the console (an "uninstall" command in the signed
   commands.json). It is an embedded resource of the agent exe, copied to a temp file for the
   run, because it has to be able to delete the folder it would otherwise be sitting in.

   It removes only what the agent itself created. Programs the agent installed OUTSIDE its own
   folder (e.g. under Program Files) are left alone; take a machine out of a program's targeting
   first if you want that program uninstalled too.
====================================================================================

.SYNOPSIS
    Uninstalls the Orchestrator agent from this PC.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$ServiceName,    # the Windows service to remove
    [Parameter(Mandatory)] [string]$InstallRoot,    # the agent's folder, deleted at the end
    [Parameter(Mandatory)] [string]$RunKey,         # registry path (under HKLM) of the startup entries
    [Parameter(Mandatory)] [string]$EntryPrefix     # name prefix that marks the agent's own tasks/entries
)

$ErrorActionPreference = "Continue"                 # keep going: a half-removed agent is worse than a noisy one
$log = Join-Path $env:TEMP "orch-uninstall.log"
function Say($m) { ("{0:u} {1}" -f (Get-Date), $m) | Add-Content -LiteralPath $log }

Say "uninstall started (service '$ServiceName', folder '$InstallRoot')"

# Never delete something that isn't plainly the agent's own folder.
$full = [System.IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
if ($full.Length -lt 8 -or $full -match '^[A-Za-z]:$' -or $full -ieq $env:SystemRoot -or $full -ieq $env:ProgramFiles -or $full -ieq ${env:ProgramFiles(x86)}) {
    Say "refusing: '$InstallRoot' doesn't look like the agent's own folder"
    exit 1
}

# 1. The service: stop it (its exe is in the folder we're about to delete), then remove it.
Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
for ($i = 0; $i -lt 30; $i++) {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $svc -or $svc.Status -eq 'Stopped') { break }
    Start-Sleep -Seconds 1
}
sc.exe delete $ServiceName | Out-Null
Say "service removed"

# 2. Scheduled tasks the agent created (startup tasks, session/screenshot one-shots, ...). Not THIS
#    run's own task: it cleans itself up a minute after it expires.
$self = $EntryPrefix + "uninstall"
Get-ScheduledTask -ErrorAction SilentlyContinue |
    Where-Object { $_.TaskName -like "$EntryPrefix*" -and $_.TaskName -ne $self } |
    ForEach-Object { Unregister-ScheduledTask -TaskName $_.TaskName -Confirm:$false -ErrorAction SilentlyContinue; Say "removed task $($_.TaskName)" }

# 3. Startup (Run key) entries.
$key = "HKLM:\$RunKey"
if (Test-Path $key) {
    (Get-Item $key).Property | Where-Object { $_ -like "$EntryPrefix*" } |
        ForEach-Object { Remove-ItemProperty -Path $key -Name $_ -ErrorAction SilentlyContinue; Say "removed startup entry $_" }
}

# 4. The folder. A process that was just stopped can hold files for a moment, so retry.
for ($i = 0; $i -lt 10; $i++) {
    try { Remove-Item -LiteralPath $full -Recurse -Force -ErrorAction Stop; Say "folder removed"; break }
    catch { Say "folder not removable yet: $($_.Exception.Message)"; Start-Sleep -Seconds 3 }
}
if (Test-Path -LiteralPath $full) { Say "WARNING: '$full' is still there; delete it by hand" }

Say "uninstall finished"
# This script was copied to a temp file just for this run.
Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
