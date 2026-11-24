<#
.SYNOPSIS
  Scans every file under a folder for personal details and secrets. Exits 1 when anything is found.
.DESCRIPTION
  Personal terms (Windows user, PC name, git email, extra -Term values) are read at runtime so this
  script holds none of them. Binary files are searched as Latin-1 and UTF-16 text, which catches
  paths embedded in PNG metadata, PDBs or DLLs.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Root,
    [string[]]$Term = @()
)

$ErrorActionPreference = 'Stop'

$terms = @($env:USERNAME, $env:COMPUTERNAME, (git config --global user.email), (git config user.email)) + $Term |
    Where-Object { $_ -and $_.Length -ge 4 } | Sort-Object -Unique
$personal = $terms | ForEach-Object { [regex]::Escape($_) }
# The local part of an email on its own (e.g. a handle used as a name)
$personal += $terms | Where-Object { $_ -like '*@*' } | ForEach-Object { [regex]::Escape($_.Split('@')[0]) }

$patterns = [ordered]@{
    'personal term'    = '(?i)(' + ($personal -join '|') + ')'
    'user profile path' = '(?i)[A-Z]:[\\/]+Users[\\/]+(?!Public\b|Default\b|<|\{|%|\$)\w'
    'local drive path' = '(?i)\b[D-Z]:[\\/]+(Projects|log-feature-archive)'
    'private key'      = '-----BEGIN [A-Z ]*PRIVATE KEY-----'
    'GitHub token'     = '\b(ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{36}\b|\bgithub_pat_[A-Za-z0-9_]{40,}'
    'Anthropic/OpenAI key' = '\bsk-(ant-)?[A-Za-z0-9_-]{32,}'
    'AWS key'          = '\bAKIA[0-9A-Z]{16}\b'
    'Google key'       = '\bAIza[0-9A-Za-z_-]{35}\b'
    'Slack token'      = '\bxox[abprs]-[A-Za-z0-9-]{10,}'
    'Azure secret'     = '(?i)(AccountKey|SharedAccessKey|client_secret)\s*[=:]\s*[A-Za-z0-9+/=]{16,}'
    'password literal' = '(?i)\bpass(word|wd)\s*[=:]\s*["''][^"''\s]{4,}'
    'secret file'      = '' # checked by name below
}
$secretNames = '(?i)\.(pfx|p12|pem|key|snk|env)$|^\.env|^id_(rsa|ed25519)|metadata\.json$'
$textExt = '(?i)\.(cs|csproj|props|targets|slnx?|json|ya?ml|md|txt|ps1|cmd|bat|py|xml|xaml|html?|css|js|svg|config|editorconfig|gitignore|gitattributes|vcxproj|filters|cpp|h|hpp|idl|def|manifest|rc|toml|ini)$'

$rootFull = (Resolve-Path -LiteralPath $Root).Path
$hits = [System.Collections.Generic.List[string]]::new()
$files = Get-ChildItem -LiteralPath $rootFull -Recurse -File -Force | Where-Object { $_.FullName.Substring($rootFull.Length) -notmatch '[\\/](\.git|\.vs|bin|obj|artifacts|TestResults)[\\/]' }

foreach ($f in $files) {
    $rel = $f.FullName.Substring($rootFull.Length + 1)
    if ($f.Name -match $secretNames) { $hits.Add("$rel : secret file name"); continue }

    $bytes = [IO.File]::ReadAllBytes($f.FullName)
    $texts = if ($f.Name -match $textExt) { @([Text.Encoding]::UTF8.GetString($bytes)) }
             else { @([Text.Encoding]::GetEncoding(28591).GetString($bytes), [Text.Encoding]::Unicode.GetString($bytes)) }

    foreach ($name in $patterns.Keys) {
        $rx = $patterns[$name]
        if (-not $rx) { continue }
        foreach ($t in $texts) {
            $ms = [regex]::Matches($t, $rx)
            if ($ms.Count -eq 0) { continue }
            if ($f.Name -match $textExt) {
                $lines = $t -split "`n"
                for ($i = 0; $i -lt $lines.Count; $i++) {
                    if ($lines[$i] -match $rx) { $hits.Add("${rel}:$($i + 1) : $name") }
                }
            } else { $hits.Add("$rel : $name ($($ms.Count) in binary)") }
            break
        }
    }
}

Write-Host "Scanned $($files.Count) files for $($terms.Count) personal terms and $($patterns.Count - 1) secret patterns."
if ($hits.Count) {
    $hits | ForEach-Object { Write-Host "  $_" }
    Write-Host "$($hits.Count) finding(s)." -ForegroundColor Red
    exit 1
}
Write-Host 'Clean.' -ForegroundColor Green
