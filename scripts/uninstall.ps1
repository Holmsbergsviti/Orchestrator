<#
.SYNOPSIS
    Stops and removes the GitHub Orchestrator Windows Service.

.PARAMETER InstallRoot   Install directory (default: C:\Orchestrator).
.PARAMETER KeepFiles     Leave files/logs on disk (default: remove them).
.PARAMETER KeepStartup   Leave Orch_* registry startup entries (default: remove them).

.EXAMPLE
    .\uninstall.ps1
#>
[CmdletBinding()]
param(
    [string]$InstallRoot = "C:\Orchestrator",
    [switch]$KeepFiles,
    [switch]$KeepStartup
)

$ErrorActionPreference = "Stop"
$ServiceName = "GitHubOrchestrator"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Must run as Administrator."
}

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host "Stopping service..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    Write-Host "Deleting service..."
    sc.exe delete $ServiceName | Out-Null
} else {
    Write-Host "Service not installed."
}

if (-not $KeepStartup) {
    $runKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"
    $props = (Get-Item $runKey).Property | Where-Object { $_ -like "Orch_*" }
    foreach ($p in $props) {
        Write-Host "Removing startup entry $p"
        Remove-ItemProperty -Path $runKey -Name $p -ErrorAction SilentlyContinue
    }
}

if (-not $KeepFiles -and (Test-Path $InstallRoot)) {
    Write-Host "Removing $InstallRoot ..."
    Remove-Item -Path $InstallRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Uninstall complete." -ForegroundColor Green
