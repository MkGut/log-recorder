<#
.SYNOPSIS
  Builds the release package for log.exe: Native AOT publish, optional code signing, zips, update manifest,
  SHA256 sums and winget manifests.
.DESCRIPTION
  Output goes to artifacts/release/<version>. Nothing is uploaded; the Release workflow attaches the files.

  Update manifest: update.json lists the version and the SHA-256 of log.exe and the bridge DLL.
  tools/Sign-UpdateManifest.ps1 then adds update.json.sig; installed copies only accept a manifest whose
  signature matches the public key in src/log.Shell/Updater.cs.

  Code signing is optional here but recommended for distribution (SmartScreen warns on unsigned exes and
  Smart App Control blocks them). Pass -CertificateThumbprint, or both -DlibPath and -MetadataPath for Azure
  Artifact Signing; build/Sign-LogBuild.ps1 does the signing and checks.
.EXAMPLE
  .\tools\Publish-Release.ps1 -Version 0.2.0
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputRoot,
    [string]$Repository = 'MkGut/log-recorder',
    [ValidateNotNullOrEmpty()]
    [string]$CertificateThumbprint,
    [ValidateNotNullOrEmpty()]
    [string]$DlibPath,
    [ValidateNotNullOrEmpty()]
    [string]$MetadataPath
)

$ErrorActionPreference = 'Stop'
if (([bool]$DlibPath -xor [bool]$MetadataPath) -or ($CertificateThumbprint -and $DlibPath)) {
    throw 'Select either -CertificateThumbprint or both -DlibPath and -MetadataPath.'
}
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptDir '..')).Path
if (-not $OutputRoot) { $OutputRoot = Join-Path $repoRoot 'artifacts\release' }
$project = Join-Path $repoRoot 'src\log.Shell\log.Shell.csproj'

if (-not $Version) {
    $Version = ([xml](Get-Content -LiteralPath $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3, got '$Version'." }

# Native AOT needs the MSVC linker; vswhere lives beside the Visual Studio installer.
$installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if ((Test-Path $installer) -and ($env:PATH -notlike "*$installer*")) { $env:PATH += ";$installer" }

$name = "log-$Version-win-x64"
$out = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) $Version
if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
$publish = Join-Path $out 'publish'
$stage = Join-Path $out 'stage'
$assets = Join-Path $out 'assets'   # everything attached to the GitHub Release
New-Item -ItemType Directory -Force $stage, $assets | Out-Null

Write-Host "Publishing log $Version (Native AOT)..."
# The version is stamped into the exe; the updater compares against it.
dotnet publish $project -c Release -o $publish "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# The shipped set is exactly the exe and the native encoder bridge.
$files = 'log.exe', 'log.MediaFoundationBridge.dll'
foreach ($f in $files) {
    $src = Join-Path $publish $f
    if (-not (Test-Path -LiteralPath $src)) { throw "Missing $f in the publish output." }
    Copy-Item -LiteralPath $src -Destination $stage
}

$signing = $CertificateThumbprint -or ($DlibPath -and $MetadataPath)
if ($signing) {
    $signArgs = @{ BuildRoot = $stage }
    if ($CertificateThumbprint) { $signArgs.CertificateThumbprint = $CertificateThumbprint }
    else { $signArgs.DlibPath = $DlibPath; $signArgs.MetadataPath = $MetadataPath }
    & (Join-Path $repoRoot 'build\Sign-LogBuild.ps1') @signArgs
    if ($LASTEXITCODE) { throw 'Signing failed.' }
}
else {
    Write-Warning 'UNSIGNED build. SmartScreen will warn and Smart App Control may block it.'
}

# Release assets: the two loose files (the updater downloads these), the zip and the portable zip.
foreach ($f in $files) { Copy-Item -LiteralPath (Join-Path $stage $f) -Destination $assets }
# The zips carry the license and notices too (the updater only replaces the two binaries).
foreach ($f in 'LICENSE', 'THIRD-PARTY-NOTICES.md') { Copy-Item -LiteralPath (Join-Path $repoRoot $f) -Destination $stage }
$zip = Join-Path $assets "$name.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
$portableStage = Join-Path $out 'portable'
New-Item -ItemType Directory -Force $portableStage | Out-Null
Copy-Item -Path (Join-Path $stage '*') -Destination $portableStage
"Portable mode: this file keeps log's recordings and settings in the data folder next to log.exe." +
    " log does not install itself or update automatically in this mode. Delete this file to use the normal install." |
    Set-Content -LiteralPath (Join-Path $portableStage 'portable.txt') -Encoding ASCII
Compress-Archive -Path (Join-Path $portableStage '*') -DestinationPath (Join-Path $assets "$name-portable.zip")

# Update manifest (compact JSON, signed over its exact bytes).
$hashes = [ordered]@{}
foreach ($f in $files) { $hashes[$f] = (Get-FileHash -LiteralPath (Join-Path $assets $f) -Algorithm SHA256).Hash }
$manifest = [ordered]@{ version = $Version; files = $hashes } | ConvertTo-Json -Compress
[IO.File]::WriteAllBytes((Join-Path $assets 'update.json'), [Text.Encoding]::UTF8.GetBytes($manifest))
# tools/Sign-UpdateManifest.ps1 adds update.json.sig (in CI, as a separate step that alone sees the key).
& (Join-Path $scriptDir 'Write-Checksums.ps1') -Assets $assets

# winget manifests from the templates (not attached; submitted to microsoft/winget-pkgs by hand)
$sha = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
$url = "https://github.com/$Repository/releases/download/v$Version/$name.zip"
$manifests = Join-Path $out 'winget'
New-Item -ItemType Directory -Force $manifests | Out-Null
foreach ($t in Get-ChildItem (Join-Path $repoRoot 'packaging\winget') -Filter '*.yaml') {
    (Get-Content -LiteralPath $t.FullName -Raw).Replace('{{VERSION}}', $Version).Replace('{{URL}}', $url).Replace('{{SHA256}}', $sha) |
        Set-Content -LiteralPath (Join-Path $manifests $t.Name) -Encoding UTF8
}

[pscustomobject]@{
    Version       = $Version
    CodeSigned    = [bool]$signing
    ExeMB         = [math]::Round((Get-Item (Join-Path $stage 'log.exe')).Length / 1MB, 2)
    Assets        = $assets
    Manifests     = $manifests
} | Format-List
