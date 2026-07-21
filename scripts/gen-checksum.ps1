<#
====================================================================================
 FILE PURPOSE (in plain terms):
   A tiny helper that takes any file and prints its unique "fingerprint" (a SHA256
   hash). You run it on each program file you add, then copy the printed
   "sha256:..." line into manifest.json. The service later re-computes this
   fingerprint after downloading a file to prove it wasn't tampered with or
   corrupted.
====================================================================================

.SYNOPSIS
    Prints the sha256:<hash> string for a file, ready to paste into manifest.json.
.EXAMPLE
    .\gen-checksum.ps1 -Path .\programs\my-app\v1.0\my-app.exe
#>
[CmdletBinding()]                                                  # enable common parameters like -Verbose
param([Parameter(Mandatory)] [string]$Path)                       # the file to fingerprint (required)
$hash = (Get-FileHash -Path $Path -Algorithm SHA256).Hash.ToLower()  # compute the SHA256 and lower-case it
"sha256:$hash"                                                     # print it in the exact format the manifest expects
