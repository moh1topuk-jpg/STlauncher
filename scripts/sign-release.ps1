#Requires -Version 5.1
<#
.SYNOPSIS
    Publishes STlauncher and signs the artifacts with a certificate from the store.

.DESCRIPTION
    Uses signtool.exe from the Windows SDK. Works with self-signed certificates and
    with certificates that have their private key in the local store.
    For HSM/token based certificates from a public CA, see docs/SIGNING.md.

.EXAMPLE
    .\scripts\sign-release.ps1 -Thumbprint 1234567890ABCDEF -Version 0.1.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Thumbprint,

    [string]$Version = '0.1.0',

    [string]$TimestampUrl = 'http://timestamp.digicert.com',

    [string]$SignToolPath
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

function Resolve-SignTool {
    param([string]$Explicit)

    if ($Explicit) {
        if (-not (Test-Path -LiteralPath $Explicit)) { throw "signtool not found at $Explicit" }
        return (Resolve-Path -LiteralPath $Explicit).Path
    }

    $onPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $kitsRoot) {
        $candidates = Get-ChildItem -LiteralPath $kitsRoot -Directory |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' }

        foreach ($candidate in $candidates) {
            if (Test-Path -LiteralPath $candidate) { return $candidate }
        }
    }

    throw 'signtool.exe not found. Install the Windows SDK or pass -SignToolPath.'
}

$signTool = Resolve-SignTool -Explicit $SignToolPath
Write-Host "Using signtool: $signTool"

Push-Location $root
try {
    Write-Host 'Publishing...'
    dotnet publish src/STlauncher.App -c Release -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none `
        -o publish

    Get-ChildItem -Path publish -Filter *.exe | ForEach-Object {
        Write-Host "Signing $($_.Name)"
        & $signTool sign /sha1 $Thumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $_.FullName
        if ($LASTEXITCODE -ne 0) { throw "signtool failed for $($_.Name)" }
    }

    Write-Host 'Packing with Velopack (signing installer payload)...'
    vpk pack -u STlauncher -v $Version -p publish -e STlauncher.App.exe -o releases `
        --signParams "/sha1 $Thumbprint /fd SHA256 /tr $TimestampUrl /td SHA256"

    Write-Host ''
    Write-Host 'Verifying signatures:'
    Get-ChildItem -Path releases -Include *.exe, *.msi -Recurse |
        ForEach-Object { & $signTool verify /pa /v $_.FullName | Out-Null; Write-Host "  $($_.Name): $($LASTEXITCODE -eq 0)" }
}
finally {
    Pop-Location
}
