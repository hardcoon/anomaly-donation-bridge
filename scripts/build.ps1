[CmdletBinding()]
param(
    [string]$DotNetRoot,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipTests,
    [string]$PublishPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$solution = Join-Path $repoRoot 'AnomalyDonationBridge.sln'
$appProject = Join-Path $repoRoot 'src\AnomalyDonationBridge.App\AnomalyDonationBridge.App.csproj'
$watchdogProject = Join-Path $repoRoot 'src\AnomalyDonationBridge.CrashGuard\AnomalyDonationBridge.CrashGuard.csproj'
$workRoot = Join-Path $repoRoot 'work\build'
$artifactsRoot = Join-Path $repoRoot 'artifacts'
if ([string]::IsNullOrWhiteSpace($PublishPath)) {
    $PublishPath = Join-Path $artifactsRoot 'portable-win-x64'
} elseif (-not [IO.Path]::IsPathRooted($PublishPath)) {
    $PublishPath = Join-Path $repoRoot $PublishPath
}
$publishRoot = [IO.Path]::GetFullPath($PublishPath)

function Resolve-DotNetExecutable {
    if (-not [string]::IsNullOrWhiteSpace($DotNetRoot)) {
        $candidate = Join-Path ([IO.Path]::GetFullPath($DotNetRoot)) 'dotnet.exe'
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            throw "dotnet.exe not found in DotNetRoot: $candidate"
        }
        return $candidate
    }

    $portable = Join-Path $repoRoot 'work\dotnet8\dotnet.exe'
    if (Test-Path -LiteralPath $portable -PathType Leaf) { return $portable }

    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $command) { throw 'A .NET 8 SDK is required.' }
    return $command.Source
}

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)
    & $script:dotnetExe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet exited with code ${LASTEXITCODE}: $($Arguments -join ' ')"
    }
}

foreach ($required in @($solution, $appProject, $watchdogProject)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required project file not found: $required"
    }
}

$dotnetExe = Resolve-DotNetExecutable
$sdkVersion = (& $dotnetExe --version 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $sdkVersion -notmatch '^8\.') {
    throw "A .NET 8 SDK is required. Reported: '$sdkVersion'."
}

$env:DOTNET_CLI_HOME = Join-Path $workRoot '.dotnet-home'
$env:NUGET_PACKAGES = Join-Path $workRoot 'packages'
$env:TEMP = Join-Path $workRoot 'temp'
$env:TMP = $env:TEMP
$env:NUGET_HTTP_CACHE_PATH = Join-Path $workRoot 'nuget-http-cache'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME,$env:NUGET_PACKAGES,$env:TEMP,$env:NUGET_HTTP_CACHE_PATH,$artifactsRoot | Out-Null

Invoke-DotNet @('restore', $solution)
Invoke-DotNet @('build', $solution, '--configuration', $Configuration, '--no-restore', '--nologo')

if (-not $SkipTests) {
    & (Join-Path $PSScriptRoot 'test.ps1') -DotNetRoot (Split-Path -Parent $dotnetExe) -Configuration $Configuration -NoRestore
}

Invoke-DotNet @('restore', $appProject, '--runtime', 'win-x64')
$resolvedArtifacts = [IO.Path]::GetFullPath($artifactsRoot).TrimEnd('\')
$resolvedPublish = [IO.Path]::GetFullPath($publishRoot)
if (-not $resolvedPublish.StartsWith($resolvedArtifacts + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "PublishPath must be inside artifacts: $resolvedPublish"
}
if (Test-Path -LiteralPath $publishRoot) {
    $existingRoot = Get-Item -LiteralPath $publishRoot -Force
    if (($existingRoot.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "PublishPath is a reparse point: $publishRoot"
    }
    $existingService = Join-Path $publishRoot '_internal'
    if ((Test-Path -LiteralPath $existingService) -and
        ((Get-Item -LiteralPath $existingService -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Observer directory is a reparse point: $existingService"
    }
    foreach ($existingName in @('AnomalyDonationBridge.exe', 'AnomalyDonationBridge.CrashGuard.exe', '_internal\AnomalyDonationBridge.CrashGuard.exe')) {
        $existingExe = Join-Path $publishRoot $existingName
        if (-not (Test-Path -LiteralPath $existingExe -PathType Leaf)) { continue }
        try {
            $exclusiveHandle = [IO.File]::Open($existingExe, [IO.FileMode]::Open,
                [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            $exclusiveHandle.Dispose()
        }
        catch [IO.IOException] {
            throw "EXE is in use. Close the running app or choose another PublishPath: $existingExe"
        }
        catch [UnauthorizedAccessException] {
            throw "EXE cannot be replaced. Close the app or choose another PublishPath: $existingExe"
        }
    }
    Remove-Item -LiteralPath $publishRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null

Invoke-DotNet @(
    'publish', $appProject,
    '--configuration', $Configuration,
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    '--no-restore',
    '--output', $publishRoot,
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:PublishTrimmed=false',
    '-p:ContinuousIntegrationBuild=true',
    '-p:Deterministic=true',
    '-p:DebugSymbols=false',
    '-p:DebugType=None'
)

$publishedExe = Join-Path $publishRoot 'AnomalyDonationBridge.exe'
if (-not (Test-Path -LiteralPath $publishedExe -PathType Leaf)) {
    throw "Published executable not found: $publishedExe"
}

$watchdogRoot = Join-Path $publishRoot '_internal'
New-Item -ItemType Directory -Force -Path $watchdogRoot | Out-Null
Invoke-DotNet @(
    'publish', $watchdogProject, '--configuration', $Configuration,
    '--runtime', 'win-x64', '--self-contained', 'true', '--no-restore',
    '--output', $watchdogRoot, '-p:ContinuousIntegrationBuild=true',
    '-p:Deterministic=true', '-p:DebugSymbols=false', '-p:DebugType=None'
)
$watchdogExe = Join-Path $watchdogRoot 'AnomalyDonationBridge.CrashGuard.exe'
if (-not (Test-Path -LiteralPath $watchdogExe -PathType Leaf) -or
    (Get-Item -LiteralPath $watchdogExe).VersionInfo.FileDescription -ne 'Anomaly Donation Bridge — Защита от вылетов') {
    throw 'Published crash observer is missing or has the wrong Task Manager description.'
}
[IO.File]::SetAttributes($watchdogRoot, ([IO.File]::GetAttributes($watchdogRoot) -bor [IO.FileAttributes]::Hidden))
if (Test-Path -LiteralPath (Join-Path $publishRoot 'AnomalyDonationBridge.CrashGuard.exe')) {
    throw 'Crash observer must not be placed alongside the main EXE.'
}

# Binary content can otherwise be bundled into the EXE while the installer
# needs it next to the manifest. Validate the actual portable directory, not
# just the test project's CopyToOutputDirectory files.
$addonRoot = Join-Path $publishRoot 'addon-package'
$addonManifest = Get-Content -LiteralPath (Join-Path $addonRoot 'addon-manifest.json') -Raw | ConvertFrom-Json
foreach ($addonFile in $addonManifest.files) {
    $assetPath = [IO.Path]::GetFullPath((Join-Path $addonRoot $addonFile.path))
    if (-not $assetPath.StartsWith($addonRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Addon manifest path escapes package: $($addonFile.path)"
    }
    if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) {
        throw "Published addon asset is missing (check ExcludeFromSingleFile): $($addonFile.path)"
    }
    if ((Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash -ne $addonFile.sha256) {
        throw "Published addon checksum mismatch: $($addonFile.path)"
    }
}

$hash = (Get-FileHash -LiteralPath $publishedExe -Algorithm SHA256).Hash
$watchdogHash = (Get-FileHash -LiteralPath $watchdogExe -Algorithm SHA256).Hash
[IO.File]::WriteAllText(
    (Join-Path $publishRoot 'ADB-APP-001.sha256'),
    "$hash *AnomalyDonationBridge.exe`n$watchdogHash *_internal/AnomalyDonationBridge.CrashGuard.exe`n",
    [Text.UTF8Encoding]::new($false))
$buildVersion = (Get-Content -LiteralPath (Join-Path $repoRoot 'version.json') -Raw |
    ConvertFrom-Json).appVersion
$record = @(
    'BuildId=ADB-APP-001',
    "Version=$buildVersion",
    "Sha256=$hash",
    "CrashGuardSha256=$watchdogHash",
    'CrashGuardPath=_internal/AnomalyDonationBridge.CrashGuard.exe',
    "Sdk=$sdkVersion",
    'Runtime=win-x64',
    "Configuration=$Configuration",
    'Protocol=AnomalyDonationBridge.v1'
) -join "`n"
[IO.File]::WriteAllText(
    (Join-Path $publishRoot 'ADB-APP-001.build.txt'),
    $record + "`n",
    [Text.UTF8Encoding]::new($false))

Write-Host "Build succeeded: $publishedExe"
Write-Host "SHA-256: $hash"
