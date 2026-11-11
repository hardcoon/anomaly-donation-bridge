[CmdletBinding()]
param(
    [string]$DotNetRoot,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$solution = Join-Path $repoRoot 'AnomalyDonationBridge.sln'
$workRoot = Join-Path $repoRoot 'work\tests'

function Resolve-DotNetExecutable {
    if (-not [string]::IsNullOrWhiteSpace($DotNetRoot)) {
        $candidate = Join-Path ([IO.Path]::GetFullPath($DotNetRoot)) 'dotnet.exe'
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
        throw "dotnet.exe not found in DotNetRoot: $candidate"
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

$dotnetExe = Resolve-DotNetExecutable
$sdkVersion = (& $dotnetExe --version 2>&1 | Out-String).Trim()
if ($sdkVersion -notmatch '^8\.') { throw "A .NET 8 SDK is required. Reported: '$sdkVersion'." }

$env:DOTNET_CLI_HOME = Join-Path $workRoot '.dotnet-home'
$env:NUGET_PACKAGES = Join-Path $repoRoot 'work\build\packages'
$env:ADB_TEST_ROOT = Join-Path $workRoot 'runtime'
$env:TEMP = Join-Path $workRoot 'temp'
$env:TMP = $env:TEMP
$env:NUGET_HTTP_CACHE_PATH = Join-Path $repoRoot 'work\build\nuget-http-cache'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME,$env:NUGET_PACKAGES,$env:ADB_TEST_ROOT,$env:TEMP,$env:NUGET_HTTP_CACHE_PATH | Out-Null

if (-not $NoRestore) { Invoke-DotNet @('restore', $solution) }
Invoke-DotNet @('build', $solution, '--configuration', $Configuration, '--no-restore', '--nologo')

$testProjects = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'tests') -Filter '*.csproj' -File -Recurse | Sort-Object FullName)
if ($testProjects.Count -eq 0) { throw 'No test projects were found.' }

foreach ($project in $testProjects) {
    $projectXml = [xml](Get-Content -LiteralPath $project.FullName -Raw)
    $outputType = @($projectXml.Project.PropertyGroup.OutputType | Where-Object { $_ }) | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace([string]$outputType) -or [string]$outputType -ne 'Exe') {
        continue
    }

    $assemblyName = [IO.Path]::GetFileNameWithoutExtension($project.Name) + '.dll'
    $binRoot = Join-Path $project.Directory.FullName "bin\$Configuration"
    $assembly = Get-ChildItem -LiteralPath $binRoot -Filter $assemblyName -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '[\\/]ref[\\/]' } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $assembly) { throw "Built test assembly not found for $($project.Name)." }

    Write-Host "Running $($project.Name)"
    Invoke-DotNet @($assembly.FullName)
}

Write-Host "All tests passed with .NET SDK $sdkVersion."
