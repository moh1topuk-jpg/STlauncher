#Requires -Version 5.1
<#
.SYNOPSIS
    Creates a free self-signed code signing certificate for local development.

.DESCRIPTION
    Self-signed certificates do NOT satisfy SmartScreen. They are useful to verify
    build integrity locally and for users who explicitly trust your certificate.
    See docs/SIGNING.md.

.EXAMPLE
    .\scripts\new-dev-cert.ps1
#>
[CmdletBinding()]
param(
    [string]$Subject = 'CN=STlauncher Development',
    [string]$OutDir = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts')
)

$ErrorActionPreference = 'Stop'

$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $Subject -and $_.HasPrivateKey } |
    Select-Object -First 1

if ($cert) {
    Write-Host "Reusing existing certificate $($cert.Thumbprint)"
}
else {
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $Subject `
        -KeyUsage DigitalSignature `
        -KeyAlgorithm RSA `
        -KeyLength 3072 `
        -HashAlgorithm SHA256 `
        -CertStoreLocation Cert:\CurrentUser\My `
        -NotAfter (Get-Date).AddYears(3) `
        -FriendlyName 'STlauncher Development'

    Write-Host "Created certificate $($cert.Thumbprint)"
}

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$publicCertPath = Join-Path $OutDir 'STlauncher-dev.cer'
Export-Certificate -Cert $cert -FilePath $publicCertPath -Type CERT | Out-Null

Write-Host ''
Write-Host "Thumbprint  : $($cert.Thumbprint)"
Write-Host "Public cert : $publicCertPath"
Write-Host ''
Write-Host 'Sign a release:'
Write-Host "  .\scripts\sign-release.ps1 -Thumbprint $($cert.Thumbprint) -Version 0.1.0"
Write-Host ''
Write-Host 'A user must import the .cer into "Trusted Root Certification Authorities"'
Write-Host 'to see the signature as valid - only do this if you trust the private key.'
