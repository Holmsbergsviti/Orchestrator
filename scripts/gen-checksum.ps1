<#
.SYNOPSIS
    Prints the sha256:<hash> string for a file, ready to paste into manifest.json.
.EXAMPLE
    .\gen-checksum.ps1 -Path .\programs\my-app\v1.0\my-app.exe
#>
[CmdletBinding()]
param([Parameter(Mandatory)] [string]$Path)
$hash = (Get-FileHash -Path $Path -Algorithm SHA256).Hash.ToLower()
"sha256:$hash"
