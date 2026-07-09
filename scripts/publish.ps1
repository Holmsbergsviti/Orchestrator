<#
.SYNOPSIS
    Publishes the orchestrator as a self-contained single-file win-x64 exe into .\publish.
.EXAMPLE
    .\publish.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$OutDir = "$PSScriptRoot\publish"
)
$ErrorActionPreference = "Stop"
$proj = Join-Path $PSScriptRoot "..\src\Orchestrator.Service\Orchestrator.Service.csproj"
dotnet publish $proj -c $Configuration -r win-x64 --self-contained true -o $OutDir
Write-Host "Published to $OutDir" -ForegroundColor Green
