#Requires -Version 5.1
<#
.SYNOPSIS
    Publishes the relay for "a server for friends" as one self-contained file per system.

.DESCRIPTION
    The relay (src/STlauncher.Relay) runs on a server, not on a player's machine. This
    builds it for linux-x64 and win-x64 as a single file that needs no .NET installed,
    and puts the results under artifacts/relay/<runtime>/. That folder is ignored by
    git: the binaries are built when needed, never committed.

    How to run the result is in docs/relay.md.

.EXAMPLE
    .\scripts\publish-relay.ps1

.EXAMPLE
    .\scripts\publish-relay.ps1 -Runtimes linux-arm64
#>
[CmdletBinding()]
param(
    [string[]]$Runtimes = @('linux-x64', 'win-x64'),
    [string]$Output
)

$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 leaves $PSScriptRoot empty inside the param block when the
# script is started with -File, so the default output folder is worked out here.
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

if (-not $Output) {
    $Output = Join-Path $root 'artifacts\relay'
}

$project = Join-Path $root 'src\STlauncher.Relay\STlauncher.Relay.csproj'

foreach ($runtime in $Runtimes) {
    $target = Join-Path $Output $runtime

    # Trimming is safe here: the relay uses sockets and nothing that is found by
    # reflection, and it takes the file from about 65 MB down to about 12.
    dotnet publish $project -c Release -r $runtime --self-contained true `
        -p:PublishSingleFile=true -p:PublishTrimmed=true -p:DebugType=none `
        -o $target --nologo

    if ($LASTEXITCODE -ne 0) {
        throw "Publishing for $runtime failed."
    }
}

Get-ChildItem -Path $Output -Recurse -File |
    ForEach-Object { '{0,7:N1} MB  {1}' -f ($_.Length / 1MB), $_.FullName }
