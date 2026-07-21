<#
====================================================================================
 FILE PURPOSE (in plain terms):
   A developer convenience script. Run it on your build machine (the one with the
   .NET SDK) to compile the Orchestrator into a single, self-contained Windows exe.
   "Self-contained" means the resulting exe carries its own .NET runtime, so the
   target machines don't need .NET installed. Output lands in .\publish.
====================================================================================

.SYNOPSIS
    Publishes the orchestrator as a self-contained single-file win-x64 exe into .\publish.
.EXAMPLE
    .\publish.ps1
#>
[CmdletBinding()]                                                        # enable common parameters
param(
    [string]$Configuration = "Release",                                 # build flavor: Release = optimized
    [string]$OutDir = "$PSScriptRoot\publish"                           # where the finished exe should go
)
$ErrorActionPreference = "Stop"                                         # stop on the first error
$proj = Join-Path $PSScriptRoot "..\src\Orchestrator.Service\Orchestrator.Service.csproj"  # path to the C# project to build
dotnet publish $proj -c $Configuration -r win-x64 --self-contained true -o $OutDir  # compile a standalone 64-bit Windows exe
Write-Host "Published to $OutDir" -ForegroundColor Green               # tell the user where the output is
