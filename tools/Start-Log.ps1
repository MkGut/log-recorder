<#
.SYNOPSIS
  Starts log.exe from artifacts/shell-publish, publishing it first when asked or when it is missing.
.DESCRIPTION
  -Rebuild      Native AOT publish of src/log.Shell, then start. Refuses while a log.exe is running (it would be locked).
  -DataRoot     Run against an isolated data folder (LOG_DATA_ROOT) so a test never touches your recordings
                and does not collide with an installed copy's single-instance lock.
  -Show         Open the window straight away instead of starting hidden in the tray.
  Nothing is ever stopped for you.
.EXAMPLE
  .\tools\Start-Log.ps1 -Rebuild -DataRoot $env:TEMP\log-test -Show
#>
[CmdletBinding()]
param(
    [switch]$Rebuild,
    [string]$DataRoot,
    [switch]$Show
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$output = Join-Path $repoRoot 'artifacts\shell-publish'
$exe = Join-Path $output 'log.exe'
$bridge = Join-Path $output 'log.MediaFoundationBridge.dll'

$running = @(Get-Process -Name 'log' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })
if ($Rebuild -or -not (Test-Path -LiteralPath $exe -PathType Leaf) -or -not (Test-Path -LiteralPath $bridge -PathType Leaf)) {
    if ($running.Count -gt 0) {
        throw "log.exe from $output is running (PID $(($running | ForEach-Object Id) -join ', ')). Exit it from its tray icon, then rebuild."
    }

    # Native AOT needs the MSVC linker; vswhere lives beside the Visual Studio installer.
    $installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
    if ((Test-Path $installer) -and ($env:PATH -notlike "*$installer*")) { $env:PATH += ";$installer" }
    $vswhere = Join-Path $installer 'vswhere.exe'
    $msbuild = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'MSBuild\Current\Bin\MSBuild.exe' | Select-Object -First 1
    if (-not $msbuild) { throw 'Visual Studio MSBuild and the x64 C++ tools are required to build the encoder bridge.' }
    & $msbuild (Join-Path $repoRoot 'src\log.Windows\MediaFoundationBridge.vcxproj') /p:Configuration=Release /p:Platform=x64 /m
    if ($LASTEXITCODE -ne 0) { throw "Encoder bridge build failed with exit code $LASTEXITCODE." }

    dotnet publish (Join-Path $repoRoot 'src\log.Shell\log.Shell.csproj') -c Release -o $output
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
    if (-not (Test-Path -LiteralPath $bridge -PathType Leaf)) { throw "Publish did not produce $bridge." }
}
elseif ($running.Count -gt 0) {
    Write-Host "log.exe is already running (PID $(($running | ForEach-Object Id) -join ', ')); nothing started."
    return
}

$previous = $env:LOG_DATA_ROOT
try {
    if ($DataRoot) {
        New-Item -ItemType Directory -Force $DataRoot | Out-Null
        $env:LOG_DATA_ROOT = (Resolve-Path $DataRoot).Path
    }

    $arguments = @(); if ($Show) { $arguments += '--show' }
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru
    Write-Host "Started log.exe (PID $($process.Id)): $exe"
}
finally {
    $env:LOG_DATA_ROOT = $previous
}
