<#
====================================================================================
 FILE PURPOSE (in plain terms):
   Pester tests for install.ps1's settings merge — the logic that decides which values
   end up in appsettings.json when you re-run the installer.

   This is tested because it has already caused one real outage. install.ps1 rewrites
   appsettings.json from scratch every run, so any setting it fails to carry over is
   silently reset to a default. That is exactly how RelayUrl kept reverting to empty,
   which disabled remote control on every machine with no error anywhere.

   The tests drive the REAL script via -ShowSettings (resolve, print, change nothing)
   rather than a copy of the logic, so they can't drift away from what actually runs.
====================================================================================
#>

BeforeAll {
    $script:RepoRoot  = Split-Path -Parent $PSScriptRoot
    $script:Installer = Join-Path $RepoRoot "scripts\install.ps1"
    $script:Defaults  = Join-Path $RepoRoot "defaults.json"

    # Run the installer in dry-run mode against a throwaway install root and hand back the
    # settings it WOULD write.
    function Invoke-Settings {
        # NOT named $Args: that's an automatic PowerShell variable, and shadowing it inside a
        # function is a reliable way to get confusing binding behaviour.
        param([hashtable]$Extra = @{}, [string]$Root)
        $all = @{ InstallRoot = $Root; DefaultsPath = $script:Defaults; ShowSettings = $true } + $Extra
        $json = & $script:Installer @all
        return ($json | Out-String | ConvertFrom-Json)
    }

    # An install root that already has settings, as a real second run would find.
    function New-ExistingInstall {
        param([hashtable]$Orchestrator)
        $root = Join-Path ([IO.Path]::GetTempPath()) ("orch-test-" + [guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Force -Path $root | Out-Null
        @{ Orchestrator = $Orchestrator } | ConvertTo-Json -Depth 5 |
            Set-Content -Path (Join-Path $root "appsettings.json") -Encoding UTF8
        return $root
    }

    $script:Installed = @{
        RootPath            = "C:\Windows\Orch"
        RepoOwner           = "acme"
        RepoName            = "control"
        Branch              = "main"
        GitHubToken         = "ghp_existing"
        SyncIntervalMinutes = 7
        IsWaker             = $true
        AutoUpdate          = $true
        RelayUrl            = "wss://10.0.0.5:5080"
        RelayCertThumbprint = "AAAABBBBCCCCDDDDEEEEFFFF0000111122223333"
    }
}

Describe "install.ps1 settings merge" {

    Context "on a machine that is already installed" {

        It "keeps every setting you did not pass" {
            $root = New-ExistingInstall $script:Installed
            try {
                # The whole point: re-pinning a certificate must not cost you the token,
                # the relay address, or the sync interval.
                $s = Invoke-Settings -Root $root -Extra @{ RelayCertThumbprint = "9999888877776666555544443333222211110000" }

                $s.RelayCertThumbprint | Should -Be "9999888877776666555544443333222211110000"
                $s.GitHubToken         | Should -Be "ghp_existing"
                $s.RelayUrl            | Should -Be "wss://10.0.0.5:5080"
                $s.SyncIntervalMinutes | Should -Be 7
                $s.RepoOwner           | Should -Be "acme"
                $s.IsWaker             | Should -BeTrue
            } finally { Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It "needs no arguments at all for a plain upgrade" {
            $root = New-ExistingInstall $script:Installed
            try {
                $s = Invoke-Settings -Root $root
                $s.RepoOwner | Should -Be "acme"
                $s.RelayUrl  | Should -Be "wss://10.0.0.5:5080"
                $s.GitHubToken | Should -Be "ghp_existing"
            } finally { Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It "lets you deliberately CLEAR a setting with an empty value" {
            $root = New-ExistingInstall $script:Installed
            try {
                # Turning remote control off must be possible. This is why the merge checks
                # whether a parameter was passed rather than whether it is empty.
                $s = Invoke-Settings -Root $root -Extra @{ RelayUrl = "" }
                $s.RelayUrl | Should -BeNullOrEmpty
                $s.GitHubToken | Should -Be "ghp_existing"   # and nothing else is disturbed
            } finally { Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It "lets you turn a boolean off and keeps it off" {
            $root = New-ExistingInstall $script:Installed
            try {
                $s = Invoke-Settings -Root $root -Extra @{ AutoUpdate = $false }
                $s.AutoUpdate | Should -BeFalse
            } finally { Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It "preserves AutoUpdate=false across a later run that doesn't mention it" {
            # A machine deliberately pinned to a build must not silently re-enable updates
            # the next time someone changes an unrelated setting.
            $pinned = $script:Installed.Clone(); $pinned.AutoUpdate = $false
            $root = New-ExistingInstall $pinned
            try {
                $s = Invoke-Settings -Root $root -Extra @{ IntervalMinutes = 15 }
                $s.AutoUpdate | Should -BeFalse
                $s.SyncIntervalMinutes | Should -Be 15
            } finally { Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }

    Context "on a fresh machine" {

        It "refuses to install without a repo, instead of writing a broken config" {
            $root = Join-Path ([IO.Path]::GetTempPath()) ("orch-test-" + [guid]::NewGuid().ToString("N"))
            { Invoke-Settings -Root $root } | Should -Throw -ExpectedMessage "*RepoOwner*"
        }

        It "falls back to defaults.json for anything not supplied" {
            $root = Join-Path ([IO.Path]::GetTempPath()) ("orch-test-" + [guid]::NewGuid().ToString("N"))
            $defaults = Get-Content $script:Defaults -Raw | ConvertFrom-Json
            $s = Invoke-Settings -Root $root -Extra @{ RepoOwner = "acme"; RepoName = "control" }

            $s.Branch              | Should -Be $defaults.defaultBranch
            $s.SyncIntervalMinutes | Should -Be ([int]$defaults.defaultSyncIntervalMinutes)
            $s.AutoUpdate          | Should -BeTrue          # updates are on unless you opt out
            $s.RelayUrl            | Should -BeNullOrEmpty   # remote control off until configured
        }
    }

    Context "when the existing config is damaged" {

        It "treats unreadable settings as a fresh install rather than failing" {
            $root = Join-Path ([IO.Path]::GetTempPath()) ("orch-test-" + [guid]::NewGuid().ToString("N"))
            New-Item -ItemType Directory -Force -Path $root | Out-Null
            Set-Content -Path (Join-Path $root "appsettings.json") -Value "{ this is not json" -Encoding UTF8
            try {
                # A corrupt file must not make the machine un-installable — you'd have no way
                # to fix it remotely.
                $s = Invoke-Settings -Root $root -Extra @{ RepoOwner = "acme"; RepoName = "control" }
                $s.RepoOwner | Should -Be "acme"
            } finally { Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }
}

Describe "update-agent.ps1 guards" {

    BeforeAll { $script:Updater = Join-Path $script:RepoRoot "scripts\update-agent.ps1" }

    It "refuses to run when the staged build is missing" {
        $empty = Join-Path ([IO.Path]::GetTempPath()) ("orch-staged-" + [guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Force -Path $empty | Out-Null
        try {
            # Never proceed to stopping the service without a binary to install.
            { & $script:Updater -SourceDir $empty -InstallRoot $empty } |
                Should -Throw -ExpectedMessage "*Staged build not found*"
        } finally { Remove-Item $empty -Recurse -Force -ErrorAction SilentlyContinue }
    }
}
