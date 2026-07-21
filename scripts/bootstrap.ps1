<#
====================================================================================
 FILE PURPOSE (in plain terms):
   This is the "one command to rule them all" installer. You paste a single line
   into an Administrator PowerShell window on a fresh Windows PC, and this script
   goes and gets the Orchestrator program off the internet and sets it up as an
   always-running background Windows Service. It either downloads a ready-made
   .exe (fast) or, if that fails, builds the .exe from the source code (slower).
   When it's done, the machine will automatically keep itself in sync with your
   GitHub control repo.
====================================================================================

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
# Marks this as an advanced script so it supports -Verbose, -ErrorAction, etc.
[CmdletBinding()]
# The values the caller can pass in when running the script.
param(
    [Parameter(Mandatory)] [string]$RepoOwner,          # who owns the control repo (required)
    [Parameter(Mandatory)] [string]$RepoName,           # name of the control repo (required)
    [string]$Token = "",                                # access token for private repos (blank = public)
    [string]$Branch = "main",                           # which branch of the control repo to read
    [int]$IntervalMinutes = 60,                          # how often the service re-checks GitHub, in minutes
    [string]$InstallRoot = "C:\Orchestrator",           # folder on disk where everything gets installed
    [string]$CodeRepo = "Holmsbergsviti/Orchestrator",  # repo that holds the exe + install scripts
    [string]$CodeRef = "main",                          # branch of that code repo to pull scripts from
    [switch]$BuildFromSource                             # if set, compile locally instead of downloading
)

$ErrorActionPreference = "Stop"  # stop the whole script the moment any command errors
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12  # force modern TLS so HTTPS downloads work on old PowerShell
$ProgressPreference = 'SilentlyContinue'   # hides the progress bar; keeps large downloads fast on Windows PowerShell 5.1

# --- Must run elevated (service creation, HKLM, icacls all need admin) ---
# Grab the identity of whoever is running this script.
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
# If they are NOT an administrator, stop with a clear message.
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this in an elevated (Administrator) PowerShell."
}

$work = Join-Path $env:TEMP ("orch-boot-" + [guid]::NewGuid().ToString("N"))  # a fresh, unique temp working folder
$pub = Join-Path $work "publish"                                             # sub-folder that will hold the finished exe
New-Item -ItemType Directory -Force -Path $pub | Out-Null                    # create that folder (Out-Null hides the output)
Write-Host "== Orchestrator bootstrap ==" -ForegroundColor Cyan             # print a banner so the user sees it started

# Downloads the ready-made exe from the repo's 'dist' branch.
function Get-Prebuilt {
    $exeUrl = "https://raw.githubusercontent.com/$CodeRepo/dist/orchestrator-service.exe"  # web address of the prebuilt exe
    $exePath = Join-Path $pub "orchestrator-service.exe"                                    # where to save it locally
    Write-Host "Downloading prebuilt service exe..."
    (New-Object System.Net.WebClient).DownloadFile($exeUrl, $exePath)                       # do the actual download
    # Sanity check: if the file is missing or suspiciously small, treat it as a failed download.
    if (-not (Test-Path $exePath) -or (Get-Item $exePath).Length -lt 1MB) {
        throw "Downloaded exe missing or too small."
    }
    Write-Host ("Downloaded {0:N1} MB." -f ((Get-Item $exePath).Length / 1MB))              # report the size in MB
}

# Fallback path: install the .NET SDK if needed, download the source, and compile the exe.
function Build-FromSource {
    # Helper that checks whether a given dotnet install actually has the Worker SDK we need.
    function Test-DotnetSdkComplete([string]$root) {
        if (-not $root -or -not (Test-Path (Join-Path $root 'sdk'))) { return $false }   # no 'sdk' folder = not complete
        return [bool](Get-ChildItem (Join-Path $root 'sdk') -Directory -ErrorAction SilentlyContinue |
            Where-Object { Test-Path (Join-Path $_.FullName 'Sdks\Microsoft.NET.Sdk.Worker') })  # true if any SDK has the Worker piece
    }

    $dotnet = $null                                                      # will hold the path to a usable dotnet.exe
    $sysDotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source  # look for a dotnet already on PATH
    if ($sysDotnet) {
        $sysRoot = Split-Path $sysDotnet                                 # the folder containing that dotnet
        # Use the system dotnet only if it has a .NET 8 SDK and that SDK is complete.
        if ((@(& $sysDotnet --list-sdks) -match '^8\.') -and (Test-DotnetSdkComplete $sysRoot)) { $dotnet = $sysDotnet }
    }
    if (-not $dotnet) {                                                  # no usable system dotnet -> install our own copy
        $dotnetDir = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet-orch' # private install location under the user profile
        # If a previous install is there but broken, delete it so we start clean.
        if ((Test-Path $dotnetDir) -and -not (Test-DotnetSdkComplete $dotnetDir)) {
            Write-Host "Local SDK is incomplete; reinstalling..."
            Remove-Item -Recurse -Force $dotnetDir -ErrorAction SilentlyContinue
        }
        if (-not (Test-DotnetSdkComplete $dotnetDir)) {                  # still not present/complete -> install it now
            Write-Host "Installing .NET 8 SDK (local to your profile)..."
            $installText = (New-Object System.Net.WebClient).DownloadString('https://dot.net/v1/dotnet-install.ps1')  # get the official installer script text
            & ([scriptblock]::Create($installText)) -Channel 8.0 -InstallDir $dotnetDir -NoPath  # run it to install .NET 8 here
        }
        if (-not (Test-DotnetSdkComplete $dotnetDir)) { throw "Could not install a complete .NET 8 SDK." }  # give up if it still failed
        $dotnet = Join-Path $dotnetDir 'dotnet.exe'                      # point at the dotnet we just installed
    }
    $env:DOTNET_ROOT = Split-Path $dotnet                               # tell the build where the .NET runtime lives
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'  # quiet, no telemetry, no first-run setup

    Write-Host "Downloading source $CodeRepo@$CodeRef ..."
    $zip = Join-Path $work "src.zip"                                    # where to save the downloaded source zip
    (New-Object System.Net.WebClient).DownloadFile("https://github.com/$CodeRepo/archive/refs/heads/$CodeRef.zip", $zip)  # download the repo as a zip
    Expand-Archive -Path $zip -DestinationPath $work -Force             # unzip it into the working folder
    $srcRoot = Get-ChildItem -Path $work -Directory | Where-Object { $_.Name -like 'Orchestrator-*' } | Select-Object -First 1  # find the extracted source folder
    if (-not $srcRoot) { throw "Could not locate extracted source." }  # bail if the unzip produced nothing expected
    $proj = Join-Path $srcRoot.FullName "src\Orchestrator.Service\Orchestrator.Service.csproj"  # path to the C# project file to build
    Write-Host "Building (a few minutes on first run)..."
    & $dotnet publish $proj -c Release -r win-x64 --self-contained true -o $pub  # compile a standalone Windows exe into $pub
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }      # stop if the compiler reported an error
}

# Decide how to obtain the exe: build locally, or download with a build fallback.
if ($BuildFromSource) {
    Build-FromSource                                                   # caller explicitly asked to compile
} else {
    try { Get-Prebuilt }                                               # normal path: just download the ready-made exe
    catch {
        # Download failed for some reason -> fall back to compiling from source.
        Write-Warning "Prebuilt download failed ($($_.Exception.Message)). Falling back to building from source..."
        Build-FromSource
    }
}

# --- Install via the repo's install.ps1 (run as a scriptblock to avoid execution policy) ---
Write-Host "Installing the Windows service..."
$installText = (New-Object System.Net.WebClient).DownloadString("https://raw.githubusercontent.com/$CodeRepo/main/scripts/install.ps1")  # fetch the install script's text
# Turn that text into a runnable block and call it, passing along all our settings + the freshly built/downloaded exe folder.
& ([scriptblock]::Create($installText)) `
    -RepoOwner $RepoOwner -RepoName $RepoName -Token $Token -Branch $Branch `
    -IntervalMinutes $IntervalMinutes -InstallRoot $InstallRoot -SourceDir $pub

Write-Host "Bootstrap complete. Logs: $InstallRoot\logs" -ForegroundColor Green  # final success message + where to find logs
