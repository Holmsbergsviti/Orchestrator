<#
.SYNOPSIS
    One-command bootstrap for a fresh Windows box: downloads the Orchestrator
    source, ensures a .NET 8 SDK, builds the self-contained service, and installs
    it as the GitHubOrchestrator Windows Service.

.DESCRIPTION
    Designed to be run straight from the web in an elevated PowerShell, e.g.:

      & ([scriptblock]::Create((irm https://raw.githubusercontent.com/Holmsbergsviti/Orchestrator/main/scripts/bootstrap.ps1))) `
          -RepoOwner Holmsbergsviti -RepoName Orchestrator-Control -Token github_pat_xxx -IntervalMinutes 1

    This builds on the target machine, which is convenient for a test VM. For a
    real fleet you would build once and distribute the published exe instead of
    installing an SDK on every machine.

.PARAMETER RepoOwner   Owner of the CONTROL repo (manifest + programs).
.PARAMETER RepoName    Name of the CONTROL repo.
.PARAMETER Token       PAT with Contents:Read on the control repo (omit if public).
.PARAMETER Branch      Control repo branch (default: main).
.PARAMETER IntervalMinutes  Sync interval (default: 60; use 1 for testing).
.PARAMETER InstallRoot Install directory (default: C:\Orchestrator).
.PARAMETER CodeRepo    Source repo to build (default: Holmsbergsviti/Orchestrator).
.PARAMETER CodeRef     Branch of the source repo (default: main).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$RepoOwner,
    [Parameter(Mandatory)] [string]$RepoName,
    [string]$Token = "",
    [string]$Branch = "main",
    [int]$IntervalMinutes = 60,
    [string]$InstallRoot = "C:\Orchestrator",
    [string]$CodeRepo = "Holmsbergsviti/Orchestrator",
    [string]$CodeRef = "main"
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# --- Must run elevated (service creation, HKLM, icacls all need admin) ---
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this in an elevated (Administrator) PowerShell."
}

$work = Join-Path $env:TEMP ("orch-boot-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $work | Out-Null
Write-Host "== Orchestrator bootstrap ==" -ForegroundColor Cyan
Write-Host "Working dir: $work"

# --- Ensure a .NET 8 SDK (installed locally, no machine-wide footprint) ---
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
$haveSdk8 = $false
if ($dotnet) { try { $haveSdk8 = @(& $dotnet --list-sdks) -match '^8\.' } catch { } }
if (-not $haveSdk8) {
    Write-Host "Installing .NET 8 SDK (local to your profile)..."
    $dotnetDir = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet-orch"
    $install = [scriptblock]::Create((Invoke-WebRequest -UseBasicParsing https://dot.net/v1/dotnet-install.ps1).Content)
    & $install -Channel 8.0 -InstallDir $dotnetDir -NoPath
    $dotnet = Join-Path $dotnetDir "dotnet.exe"
}
Write-Host "Using dotnet: $dotnet"

# --- Download source as a zip (no git required) ---
Write-Host "Downloading source $CodeRepo@$CodeRef ..."
$zip = Join-Path $work "src.zip"
Invoke-WebRequest -UseBasicParsing "https://github.com/$CodeRepo/archive/refs/heads/$CodeRef.zip" -OutFile $zip
Expand-Archive -Path $zip -DestinationPath $work -Force
$srcRoot = Get-ChildItem -Path $work -Directory | Where-Object { $_.Name -like 'Orchestrator-*' } | Select-Object -First 1
if (-not $srcRoot) { throw "Could not locate extracted source under $work." }

# --- Build the self-contained service ---
$proj = Join-Path $srcRoot.FullName "src\Orchestrator.Service\Orchestrator.Service.csproj"
$pub = Join-Path $work "publish"
Write-Host "Building (this can take a few minutes on first run)..."
& $dotnet publish $proj -c Release -r win-x64 --self-contained true -o $pub
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }

# --- Install via the repo's install.ps1 (run as a scriptblock to avoid execution policy) ---
Write-Host "Installing the Windows service..."
$installText = Get-Content -Raw (Join-Path $srcRoot.FullName "scripts\install.ps1")
& ([scriptblock]::Create($installText)) `
    -RepoOwner $RepoOwner -RepoName $RepoName -Token $Token -Branch $Branch `
    -IntervalMinutes $IntervalMinutes -InstallRoot $InstallRoot -SourceDir $pub

Write-Host "Bootstrap complete. Logs: $InstallRoot\logs" -ForegroundColor Green
