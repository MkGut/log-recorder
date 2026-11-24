[CmdletBinding()]
param(
    [switch]$SkipRestore,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$environmentNames = @(
    'DOTNET_CLI_TELEMETRY_OPTOUT',
    'DOTNET_SKIP_FIRST_TIME_EXPERIENCE',
    'DOTNET_GENERATE_ASPNET_CERTIFICATE',
    'NUGET_PACKAGES',
    'NUGET_HTTP_CACHE_PATH',
    'DOTNET_CLI_HOME'
)
$previousErrorActionPreference = $ErrorActionPreference
$previousEnvironment = @{}
foreach ($name in $environmentNames) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

try {
    $ErrorActionPreference = 'Stop'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $solutionPath = Join-Path $PSScriptRoot '..\log.slnx'
    $configPath = Join-Path $PSScriptRoot '..\NuGet.Config'
    $cacheRoot = Join-Path $env:TEMP 'log-verify-cache-daily-main-2026-11-06'
    $packagesPath = Join-Path $cacheRoot 'packages'
    $httpCachePath = Join-Path $cacheRoot 'http-cache'
    $dotnetCliHome = Join-Path $cacheRoot 'dotnet-cli-home'
    New-Item -ItemType Directory -Path $packagesPath, $httpCachePath, $dotnetCliHome -Force | Out-Null
    $env:NUGET_PACKAGES = $packagesPath
    $env:NUGET_HTTP_CACHE_PATH = $httpCachePath
    $env:DOTNET_CLI_HOME = $dotnetCliHome

    if (-not $SkipRestore) {
        dotnet restore $solutionPath --configfile $configPath --packages $packagesPath
        if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }
    }
    else {
        [xml]$solution = [IO.File]::ReadAllText($solutionPath)
        $repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
        $packagesRoot = [IO.Path]::GetFullPath($packagesPath).TrimEnd([char[]]@('\', '/'))
        $packagesRootPrefix = $packagesRoot + [IO.Path]::DirectorySeparatorChar
        foreach ($project in $solution.SelectNodes('//Project')) {
            $projectPath = Join-Path $repositoryRoot ($project.GetAttribute('Path'))
            $assetsPath = Join-Path (Split-Path -Parent $projectPath) 'obj\project.assets.json'
            if (-not (Test-Path -LiteralPath $assetsPath -PathType Leaf)) {
                throw "-SkipRestore requires existing project assets restored under '$packagesPath'; '$assetsPath' is missing."
            }

            $assets = [IO.File]::ReadAllText($assetsPath) | ConvertFrom-Json
            $packageFolders = @($assets.packageFolders.PSObject.Properties.Name)
            if ($packageFolders.Count -eq 0) {
                throw "-SkipRestore cannot verify the package cache location in '$assetsPath'."
            }
            foreach ($packageFolder in $packageFolders) {
                $packageFolderPath = [IO.Path]::GetFullPath($packageFolder)
                if ($packageFolderPath -ne $packagesRoot -and
                    -not $packageFolderPath.StartsWith($packagesRootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "-SkipRestore refuses project assets that point outside '%TEMP%': '$assetsPath' references '$packageFolderPath'. Run without -SkipRestore."
                }
            }
        }
    }

    $shellProjectPath = Join-Path $PSScriptRoot '..\src\log.Shell\log.Shell.csproj'
    $nativeProjectPath = Join-Path $PSScriptRoot '..\src\log.Windows\MediaFoundationBridge.vcxproj'
    $nativeDllPath = Join-Path $PSScriptRoot '..\src\log.Windows\bin\Release\x64\log.MediaFoundationBridge.dll'
    $vswherePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswherePath -PathType Leaf)) {
        throw "Shell verification requires Visual Studio's vswhere.exe to locate and validate the native toolchain ('$vswherePath' was not found)."
    }

    $vcInstallationPath = (& $vswherePath `
            -latest `
            -products '*' `
            -requires 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' `
            -property installationPath |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -First 1)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($vcInstallationPath)) {
        throw 'Shell verification requires the Visual C++ x86/x64 build tools component (Microsoft.VisualStudio.Component.VC.Tools.x86.x64). Install that component before running the native/shell gate.'
    }

    $msbuildPath = Join-Path $vcInstallationPath 'MSBuild\Current\Bin\MSBuild.exe'
    if (-not (Test-Path -LiteralPath $msbuildPath -PathType Leaf)) {
        throw "The Visual Studio installation with VC.Tools.x86.x64 does not contain '$msbuildPath'."
    }

    Write-Host 'Building the x64 Media Foundation bridge required by the shell gate...'
    & $msbuildPath $nativeProjectPath /p:Configuration=Release /p:Platform=x64 /m
    if ($LASTEXITCODE -ne 0) {
        throw "Native Media Foundation bridge verification failed with exit code $LASTEXITCODE."
    }

    if (-not (Test-Path -LiteralPath $nativeDllPath -PathType Leaf)) {
        throw "Native Media Foundation bridge verification reported success but did not produce '$nativeDllPath'."
    }

    dotnet build $solutionPath --configuration $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }
    dotnet test $solutionPath --configuration $Configuration --no-build --no-restore --logger 'console;verbosity=minimal'
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE." }

    # log.Shell is in the solution (built and tested above); this publishes the real Native AOT log.exe,
    # which surfaces trim/AOT warnings and proves the native bridge sits beside it.
    Write-Host 'Publishing log.exe (Native AOT)...'
    $vswhereDirectory = Split-Path -Parent $vswherePath
    if ($env:PATH -notlike "*$vswhereDirectory*") { $env:PATH += ";$vswhereDirectory" }
    $verifyPublish = Join-Path $PSScriptRoot '..\artifacts\verify-publish'
    dotnet publish $shellProjectPath --configuration Release --output $verifyPublish --nologo --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw "log.exe Native AOT publish failed with exit code $LASTEXITCODE." }
    foreach ($required in 'log.exe', 'log.MediaFoundationBridge.dll') {
        if (-not (Test-Path -LiteralPath (Join-Path $verifyPublish $required) -PathType Leaf)) {
            throw "The Native AOT publish did not produce $required."
        }
    }
}
finally {
    $ErrorActionPreference = $previousErrorActionPreference
    foreach ($name in $environmentNames) {
        if ($null -eq $previousEnvironment[$name]) {
            Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
        }
        else {
            Set-Item -LiteralPath "Env:$name" -Value $previousEnvironment[$name]
        }
    }
}
