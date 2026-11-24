[CmdletBinding(SupportsShouldProcess, DefaultParameterSetName = 'Certificate')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Certificate')]
    [Parameter(Mandatory, ParameterSetName = 'AzureArtifactSigning')]
    [Parameter(Mandatory, ParameterSetName = 'Inventory')]
    [ValidateNotNullOrEmpty()]
    [string]$BuildRoot,

    [Parameter(Mandatory, ParameterSetName = 'Certificate')]
    [string]$CertificateThumbprint,

    [Parameter(Mandatory, ParameterSetName = 'AzureArtifactSigning')]
    [ValidateNotNullOrEmpty()]
    [string]$DlibPath,

    [Parameter(Mandatory, ParameterSetName = 'AzureArtifactSigning')]
    [ValidateNotNullOrEmpty()]
    [string]$MetadataPath,

    [Parameter(Mandatory, ParameterSetName = 'Inventory')]
    [switch]$InventoryOnly,

    [switch]$IncludeRedistributedDependencies,

    [ValidateNotNullOrEmpty()]
    [string]$TimestampUrl = 'http://timestamp.acs.microsoft.com'
)

$ErrorActionPreference = 'Stop'

function Get-SafeBuildRoot([string]$Path) {
    $resolved = (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path
    $item = Get-Item -LiteralPath $resolved -Force
    if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "BuildRoot must be a real directory, not a file or reparse point: $resolved"
    }
    return $resolved.TrimEnd('\')
}

function Assert-SafeFile([string]$Path, [string]$Root) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Refusing to sign a reparse-point file: $Path"
    }
    $full = [IO.Path]::GetFullPath($item.FullName)
    $prefix = "$Root\"
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Signing target escapes BuildRoot: $full"
    }
}

function Assert-NonReparseFile([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Signing provider paths must be regular files: $Path"
    }
}

function Find-SignTool {
    $onPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $kitsRoot) {
        $candidate = Get-ChildItem -LiteralPath $kitsRoot -Directory -Filter '10.*' |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
            Where-Object { Test-Path -LiteralPath $_ } |
            Select-Object -First 1
        if ($candidate) { return $candidate }
    }

    throw 'signtool.exe was not found on PATH or under the Windows 10 SDK x64 bin directory.'
}

$root = Get-SafeBuildRoot $BuildRoot
$signTool = Find-SignTool
# The Native AOT publish is one exe plus the native encoder bridge; nothing else is shipped.
$knownNames = @(
    'log.exe',
    'log.MediaFoundationBridge.dll'
)
$redistributedNames = @()
$approvedNames = @($knownNames)
if ($IncludeRedistributedDependencies) { $approvedNames += $redistributedNames }

$pendingDirectories = [Collections.Generic.Queue[string]]::new()
$pendingDirectories.Enqueue($root)
$allBinaries = @(while ($pendingDirectories.Count -gt 0) {
    foreach ($entry in Get-ChildItem -LiteralPath $pendingDirectories.Dequeue() -Force) {
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Reparse points are not permitted in a signing build: $($entry.FullName)"
        }
        if ($entry.PSIsContainer) { $pendingDirectories.Enqueue($entry.FullName) }
        elseif ($entry.Extension -in '.exe', '.dll') { $entry }
    }
})
$targets = @($allBinaries | Where-Object { $approvedNames -contains $_.Name })
$unexpected = @($allBinaries | Where-Object { $approvedNames -notcontains $_.Name })
$statusByPath = @{}
foreach ($binary in $allBinaries) {
    $statusByPath[$binary.FullName] = Get-AuthenticodeSignature -LiteralPath $binary.FullName
}
if ($unexpected.Count -gt 0) {
    $unsigned = @($unexpected | Where-Object {
        $statusByPath[$_.FullName].Status -ne 'Valid'
    })
    if ($unsigned.Count -gt 0 -and -not $InventoryOnly) {
        throw "Unsigned or invalid third-party binaries are present; refusing to sign them: $($unsigned.FullName -join ', ')"
    }
    Write-Verbose "Verified $($unexpected.Count) non-first-party binaries already have valid signatures."
}

if ($InventoryOnly) {
    foreach ($binary in $allBinaries) {
        [pscustomobject]@{
            File = $binary.FullName.Substring($root.Length + 1)
            Status = [string]$statusByPath[$binary.FullName].Status
            ApprovedTarget = $approvedNames -contains $binary.Name
            SHA256 = (Get-FileHash -LiteralPath $binary.FullName -Algorithm SHA256).Hash
        }
    }
    return
}

foreach ($requiredName in $knownNames) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $requiredName) -PathType Leaf)) {
        throw "Incomplete build: missing $requiredName"
    }
}
foreach ($target in $targets) {
    if ($statusByPath[$target.FullName].Status -notin 'Valid', 'NotSigned') {
        throw "Refusing to replace an invalid signature: $($target.FullName)"
    }
}
if ($targets.Count -eq 0) { throw "No known log binaries were found below BuildRoot: $root" }
foreach ($target in $targets) { Assert-SafeFile $target.FullName $root }
$signTargets = @($targets | Where-Object { $statusByPath[$_.FullName].Status -ne 'Valid' })

$signArgs = @('sign', '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256')
if ($PSCmdlet.ParameterSetName -eq 'Certificate') {
    if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
        throw 'CertificateThumbprint is required for the Certificate parameter set.'
    }
    $normalizedThumbprint = $CertificateThumbprint -replace '\s', ''
    $cert = Get-ChildItem -Path "Cert:\CurrentUser\My\$normalizedThumbprint" -ErrorAction Stop
    if (-not $cert.HasPrivateKey) { throw "The selected certificate has no private key: $normalizedThumbprint" }
    if ($cert.NotBefore -gt (Get-Date) -or $cert.NotAfter -le (Get-Date)) { throw 'Certificate is not currently valid.' }
    if ($cert.Subject -eq $cert.Issuer) { throw 'Self-signed certificates do not satisfy Smart App Control public trust.' }
    if ($cert.PublicKey.Oid.Value -ne '1.2.840.113549.1.1.1') { throw 'Smart App Control requires an RSA certificate.' }
    if ('1.3.6.1.5.5.7.3.3' -notin @($cert.EnhancedKeyUsageList | ForEach-Object { $_.ObjectId.Value })) {
        throw 'Certificate does not have the code-signing EKU.'
    }
    $signArgs += @('/sha1', $normalizedThumbprint)
} elseif ($PSCmdlet.ParameterSetName -eq 'AzureArtifactSigning') {
    Assert-NonReparseFile $DlibPath
    Assert-NonReparseFile $MetadataPath
    $metadata = Get-Content -LiteralPath $MetadataPath -Raw | ConvertFrom-Json
    foreach ($field in 'Endpoint', 'CodeSigningAccountName', 'CertificateProfileName') {
        if ([string]::IsNullOrWhiteSpace($metadata.$field) -or $metadata.$field -match '[<>]') {
            throw "Signing metadata requires a real $field value."
        }
    }
    $signArgs += @('/dlib', (Resolve-Path -LiteralPath $DlibPath).Path, '/dmdf', (Resolve-Path -LiteralPath $MetadataPath).Path)
} else {
    throw 'Select either -CertificateThumbprint or both -DlibPath and -MetadataPath.'
}

Write-Host "signtool: $signTool"
Write-Host "BuildRoot: $root"
Write-Host "First-party targets: $($targets.Count)"
Write-Host "Unsigned targets to sign: $($signTargets.Count)"
if (-not $InventoryOnly -and $signTargets.Count -eq 0) {
    Write-Host 'All approved binaries already have valid signatures.'
    return
}
foreach ($target in $signTargets) {
    $arguments = @($signArgs + $target.FullName)
    if ($PSCmdlet.ShouldProcess($target.FullName, 'Authenticode sign with SHA256 and RFC3161 timestamp')) {
        & $signTool @arguments
        if ($LASTEXITCODE -ne 0) { throw "signtool failed for $($target.FullName) with exit code $LASTEXITCODE" }
    } else {
        Write-Host "WHATIF: signtool $($arguments -join ' ')"
    }
}

if (-not $WhatIfPreference) {
    foreach ($target in $targets) {
        & $signTool verify /pa /all $target.FullName
        if ($LASTEXITCODE -ne 0) { throw "Authenticode verification failed: $($target.FullName)" }
        $signature = Get-AuthenticodeSignature -LiteralPath $target.FullName
        if ($signature.Status -ne 'Valid') {
            throw "Signature verification failed for $($target.FullName): $($signature.Status)"
        }
        if ($target -in $signTargets -and $null -eq $signature.TimeStamperCertificate) {
            throw "New signature has no timestamp: $($target.FullName)"
        }
    }
    Write-Host 'All first-party signing targets verified.'
}
