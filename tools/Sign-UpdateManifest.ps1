<#
.SYNOPSIS
  Signs update.json in a release assets folder, writing update.json.sig, then refreshes SHA256SUMS.txt.
.DESCRIPTION
  The PKCS#8 PEM private key comes from the LOG_UPDATE_SIGNING_KEY environment variable (the
  UPDATE_SIGNING_KEY secret in CI). Signature: ECDSA P-256 over the exact file bytes, SHA-256, IEEE P1363
  format, base64. Installed copies verify it with the public key in src/log.Shell/Updater.cs.
  Needs PowerShell 7 (pwsh).
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Assets)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Run this with PowerShell 7 (pwsh).' }
if (-not $env:LOG_UPDATE_SIGNING_KEY) { throw 'LOG_UPDATE_SIGNING_KEY is not set; installed copies would ignore this release.' }

$manifestPath = Join-Path $Assets 'update.json'
$bytes = [IO.File]::ReadAllBytes($manifestPath)
$key = [Security.Cryptography.ECDsa]::Create()
try {
    $key.ImportFromPem($env:LOG_UPDATE_SIGNING_KEY)
    # A key that does not match the one built into the app would silently stop every update.
    $updater = Get-Content -Raw (Join-Path $PSScriptRoot '..\src\log.Shell\Updater.cs')
    $shipped = [regex]::Match($updater, 'PublicKey\s*=\s*"([^"]+)"').Groups[1].Value
    if ([Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo()) -ne $shipped) { throw 'The signing key does not match Updater.PublicKey.' }
    $sig = $key.SignData($bytes, [Security.Cryptography.HashAlgorithmName]::SHA256)
    if (-not $key.VerifyData($bytes, $sig, [Security.Cryptography.HashAlgorithmName]::SHA256)) { throw 'Signature did not verify.' }
}
finally { $key.Dispose() }

[IO.File]::WriteAllText((Join-Path $Assets 'update.json.sig'), [Convert]::ToBase64String($sig))
& (Join-Path $PSScriptRoot 'Write-Checksums.ps1') -Assets $Assets
Write-Host "Signed $manifestPath"
