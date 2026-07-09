<#
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
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$RepoOwner,
    [Parameter(Mandatory)] [string]$RepoName,
    [string]$Token = "",
    [string]$Branch = "main",
    [int]$IntervalMinutes = 60,
    [string]$InstallRoot = "C:\Orchestrator",
    [string]$SourceDir = "$PSScriptRoot\publish"
)

$ErrorActionPreference = "Stop"
$ServiceName = "GitHubOrchestrator"
$ExeName     = "orchestrator-service.exe"

# --- Elevation check ---
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Must run as Administrator."
}

if (-not (Test-Path (Join-Path $SourceDir $ExeName))) {
    throw "Published binary not found: $(Join-Path $SourceDir $ExeName). Run: dotnet publish -c Release -r win-x64"
}

Write-Host "Installing to $InstallRoot ..."
New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null

# Stop existing service before overwriting files.
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host "Stopping existing service..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

Copy-Item -Path (Join-Path $SourceDir '*') -Destination $InstallRoot -Recurse -Force

# --- Write settings ---
$exePath  = Join-Path $InstallRoot $ExeName
$settings = Join-Path $InstallRoot "appsettings.json"
$config = [ordered]@{
    Orchestrator = [ordered]@{
        RootPath            = $InstallRoot
        RepoOwner           = $RepoOwner
        RepoName            = $RepoName
        Branch              = $Branch
        ManifestPath        = "manifest.json"
        GitHubToken         = $Token
        SyncIntervalMinutes = $IntervalMinutes
        StartupRegistryKey  = "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"
        RegistryEntryPrefix = "Orch_"
    }
}
$config | ConvertTo-Json -Depth 5 | Set-Content -Path $settings -Encoding UTF8

# Restrict directory permissions: SYSTEM + Administrators only.
icacls $InstallRoot /inheritance:r /grant:r "SYSTEM:(OI)(CI)F" "Administrators:(OI)(CI)F" | Out-Null

# --- Create / update service ---
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host "Updating service binary path..."
    sc.exe config $ServiceName binPath= "`"$exePath`"" start= auto | Out-Null
} else {
    Write-Host "Creating service..."
    New-Service -Name $ServiceName -BinaryPathName "`"$exePath`"" `
        -DisplayName "GitHub Orchestrator" -StartupType Automatic `
        -Description "Syncs and manages programs from a GitHub manifest." | Out-Null
}

# Auto-restart on failure.
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null

Write-Host "Starting service..."
Start-Service -Name $ServiceName
Write-Host "Done. Service '$ServiceName' is running. Logs: $InstallRoot\logs" -ForegroundColor Green
