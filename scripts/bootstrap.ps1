<#
.SYNOPSIS
    One-command install for a Windows box: downloads the prebuilt Orchestrator
    service exe and installs it as the GitHubOrchestrator Windows Service.

.DESCRIPTION
    Run straight from the web in an elevated PowerShell, e.g.:

      & ([scriptblock]::Create((irm https://raw.githubusercontent.com/Holmsbergsviti/Orchestrator/main/scripts/bootstrap.ps1))) `
          -RepoOwner Holmsbergsviti -RepoName Orchestrator-Control -Token github_pat_xxx -IntervalMinutes 1

    By default it downloads the prebuilt exe from the repo's 'dist' branch (fast,
    no SDK, no build). Pass -BuildFromSource to build locally instead.

.PARAMETER RepoOwner   Owner of the CONTROL repo (manifest + programs).
.PARAMETER RepoName    Name of the CONTROL repo.
.PARAMETER Token       PAT with Contents:Read on the control repo (omit if public).
.PARAMETER Branch      Control repo branch (default: main).
.PARAMETER IntervalMinutes  Sync interval (default: 60; use 1 for testing).
.PARAMETER InstallRoot Install directory (default: C:\Orchestrator).
.PARAMETER CodeRepo    Source repo hosting the exe + scripts (default: Holmsbergsviti/Orchestrator).
.PARAMETER CodeRef     Branch of the source repo for scripts (default: main).
.PARAMETER BuildFromSource  Build the exe locally instead of downloading it.
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
    [string]$CodeRef = "main",
    [switch]$BuildFromSource
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$ProgressPreference = 'SilentlyContinue'   # keeps large downloads fast on Windows PowerShell 5.1

# --- Must run elevated (service creation, HKLM, icacls all need admin) ---
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this in an elevated (Administrator) PowerShell."
}

$work = Join-Path $env:TEMP ("orch-boot-" + [guid]::NewGuid().ToString("N"))
$pub = Join-Path $work "publish"
New-Item -ItemType Directory -Force -Path $pub | Out-Null
Write-Host "== Orchestrator bootstrap ==" -ForegroundColor Cyan

function Get-Prebuilt {
    $exeUrl = "https://raw.githubusercontent.com/$CodeRepo/dist/orchestrator-service.exe"
    $exePath = Join-Path $pub "orchestrator-service.exe"
    Write-Host "Downloading prebuilt service exe..."
    (New-Object System.Net.WebClient).DownloadFile($exeUrl, $exePath)
    if (-not (Test-Path $exePath) -or (Get-Item $exePath).Length -lt 1MB) {
        throw "Downloaded exe missing or too small."
    }
    Write-Host ("Downloaded {0:N1} MB." -f ((Get-Item $exePath).Length / 1MB))
}

function Build-FromSource {
    function Test-DotnetSdkComplete([string]$root) {
        if (-not $root -or -not (Test-Path (Join-Path $root 'sdk'))) { return $false }
        return [bool](Get-ChildItem (Join-Path $root 'sdk') -Directory -ErrorAction SilentlyContinue |
            Where-Object { Test-Path (Join-Path $_.FullName 'Sdks\Microsoft.NET.Sdk.Worker') })
    }

    $dotnet = $null
    $sysDotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
    if ($sysDotnet) {
        $sysRoot = Split-Path $sysDotnet
        if ((@(& $sysDotnet --list-sdks) -match '^8\.') -and (Test-DotnetSdkComplete $sysRoot)) { $dotnet = $sysDotnet }
    }
    if (-not $dotnet) {
        $dotnetDir = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet-orch'
        if ((Test-Path $dotnetDir) -and -not (Test-DotnetSdkComplete $dotnetDir)) {
            Write-Host "Local SDK is incomplete; reinstalling..."
            Remove-Item -Recurse -Force $dotnetDir -ErrorAction SilentlyContinue
        }
        if (-not (Test-DotnetSdkComplete $dotnetDir)) {
            Write-Host "Installing .NET 8 SDK (local to your profile)..."
            $installText = (New-Object System.Net.WebClient).DownloadString('https://dot.net/v1/dotnet-install.ps1')
            & ([scriptblock]::Create($installText)) -Channel 8.0 -InstallDir $dotnetDir -NoPath
        }
        if (-not (Test-DotnetSdkComplete $dotnetDir)) { throw "Could not install a complete .NET 8 SDK." }
        $dotnet = Join-Path $dotnetDir 'dotnet.exe'
    }
    $env:DOTNET_ROOT = Split-Path $dotnet
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'

    Write-Host "Downloading source $CodeRepo@$CodeRef ..."
    $zip = Join-Path $work "src.zip"
    (New-Object System.Net.WebClient).DownloadFile("https://github.com/$CodeRepo/archive/refs/heads/$CodeRef.zip", $zip)
    Expand-Archive -Path $zip -DestinationPath $work -Force
    $srcRoot = Get-ChildItem -Path $work -Directory | Where-Object { $_.Name -like 'Orchestrator-*' } | Select-Object -First 1
    if (-not $srcRoot) { throw "Could not locate extracted source." }
    $proj = Join-Path $srcRoot.FullName "src\Orchestrator.Service\Orchestrator.Service.csproj"
    Write-Host "Building (a few minutes on first run)..."
    & $dotnet publish $proj -c Release -r win-x64 --self-contained true -o $pub
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }
}

if ($BuildFromSource) {
    Build-FromSource
} else {
    try { Get-Prebuilt }
    catch {
        Write-Warning "Prebuilt download failed ($($_.Exception.Message)). Falling back to building from source..."
        Build-FromSource
    }
}

# --- Install via the repo's install.ps1 (run as a scriptblock to avoid execution policy) ---
Write-Host "Installing the Windows service..."
$installText = (New-Object System.Net.WebClient).DownloadString("https://raw.githubusercontent.com/$CodeRepo/main/scripts/install.ps1")
& ([scriptblock]::Create($installText)) `
    -RepoOwner $RepoOwner -RepoName $RepoName -Token $Token -Branch $Branch `
    -IntervalMinutes $IntervalMinutes -InstallRoot $InstallRoot -SourceDir $pub

Write-Host "Bootstrap complete. Logs: $InstallRoot\logs" -ForegroundColor Green
