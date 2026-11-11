[CmdletBinding()]
param([string]$Channel = '8.0')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$workRoot = Join-Path $repoRoot 'work'
$installRoot = Join-Path $workRoot 'dotnet8'
$installer = Join-Path $workRoot 'dotnet-install.ps1'
$env:TEMP = Join-Path $workRoot 'bootstrap-temp'
$env:TMP = $env:TEMP
$env:DOTNET_CLI_HOME = Join-Path $workRoot '.dotnet-home'
$env:NUGET_PACKAGES = Join-Path $workRoot 'build\packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $workRoot 'build\nuget-http-cache'

New-Item -ItemType Directory -Force -Path $workRoot,$installRoot,$env:TEMP,$env:DOTNET_CLI_HOME,$env:NUGET_PACKAGES,$env:NUGET_HTTP_CACHE_PATH | Out-Null
Invoke-WebRequest -UseBasicParsing -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer `
    -Channel $Channel -Architecture x64 -InstallDir $installRoot -NoPath
if ($LASTEXITCODE -ne 0) { throw "dotnet-install exited with code $LASTEXITCODE." }

$dotnet = Join-Path $installRoot 'dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw "Portable SDK installation did not produce $dotnet."
}
& $dotnet --version
