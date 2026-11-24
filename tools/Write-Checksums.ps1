<#
.SYNOPSIS
  Writes SHA256SUMS.txt (sha256sum format) for every file in a release assets folder.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Assets)

$ErrorActionPreference = 'Stop'
Get-ChildItem -LiteralPath $Assets -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
} | Set-Content -LiteralPath (Join-Path $Assets 'SHA256SUMS.txt') -Encoding ASCII
