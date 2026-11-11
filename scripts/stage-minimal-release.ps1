[CmdletBinding()]
param(
    [string]$Version = '1.2.1-rel',
    [int]$ExpectedEffectCount = 65,
    [string]$PublishedRoot,
    [string]$ReadmePath,
    [string]$ReleaseNotesPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifactsRoot = Join-Path $projectRoot 'artifacts'
if ([string]::IsNullOrWhiteSpace($PublishedRoot)) {
    $PublishedRoot = Join-Path $artifactsRoot "portable-win-x64-$Version-local"
}
$publishedRoot = [IO.Path]::GetFullPath($PublishedRoot)
$releaseRoot = Join-Path $artifactsRoot "release-$Version"
$stageRoot = Join-Path $releaseRoot 'stage'
$minimalZip = Join-Path $releaseRoot "AnomalyDonationBridge-$Version-win-x64.zip"

function Assert-InArtifacts([string]$path) {
    $candidate = [IO.Path]::GetFullPath($path)
    $parent = [IO.Path]::GetFullPath($artifactsRoot).TrimEnd('\')
    if (-not $candidate.StartsWith($parent + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release output must stay inside artifacts: $candidate"
    }
    if (Test-Path -LiteralPath $candidate) {
        $item = Get-Item -LiteralPath $candidate -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Release output is a reparse point: $candidate"
        }
    }
}

Assert-InArtifacts $releaseRoot
Assert-InArtifacts $stageRoot
Assert-InArtifacts $publishedRoot
if (-not (Test-Path -LiteralPath (Join-Path $publishedRoot 'AnomalyDonationBridge.exe') -PathType Leaf)) {
    throw 'Build the portable EXE before staging a release.'
}
if (-not (Test-Path -LiteralPath (Join-Path $publishedRoot '_internal\AnomalyDonationBridge.CrashGuard.exe') -PathType Leaf)) {
    throw 'The named crash observer must be included with the portable app.'
}
if ((Get-ChildItem -LiteralPath (Join-Path $publishedRoot 'effects\gamedata\scripts') -File).Count -ne $ExpectedEffectCount) {
    throw 'Unexpected one-file preset Lua count.'
}
$releaseVersion = Get-Content -LiteralPath (Join-Path $projectRoot 'version.json') -Raw | ConvertFrom-Json
$releaseAddonRoot = Join-Path $publishedRoot 'addon-package'
$manifestPath = Join-Path $releaseAddonRoot 'addon-manifest.json'
$sourceManifest = Join-Path $projectRoot 'game-addon\addon-manifest.json'
if ((Get-FileHash -LiteralPath $manifestPath).Hash -ne (Get-FileHash -LiteralPath $sourceManifest).Hash) {
    throw 'The packaged addon manifest differs from the source manifest.'
}
$releaseAddon = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$expectedPaths = @('addon-manifest.json') + @($releaseAddon.files.path)
$packagedPaths = @(Get-ChildItem -LiteralPath $releaseAddonRoot -File -Recurse -Force | ForEach-Object {
    $_.FullName.Substring($releaseAddonRoot.Length + 1).Replace('\','/')
})
if (Compare-Object $expectedPaths $packagedPaths) {
    throw 'The packaged addon file list differs from its manifest.'
}
if ($releaseVersion.appVersion -ne $Version -or $releaseVersion.addonVersion -ne $releaseAddon.addonVersion) {
    throw 'Release version does not match the application/addon pair.'
}
foreach ($releaseEntry in $releaseAddon.files) {
    $releaseAsset = [IO.Path]::GetFullPath((Join-Path $releaseAddonRoot $releaseEntry.path))
    if (-not $releaseAsset.StartsWith($releaseAddonRoot + '\',[StringComparison]::OrdinalIgnoreCase) -or
        (Get-FileHash -LiteralPath $releaseAsset).Hash -ne $releaseEntry.sha256) {
        throw "Invalid release addon asset: $($releaseEntry.path)"
    }
}
$releaseSpeeds = @(Get-ChildItem -LiteralPath (Join-Path $releaseAddonRoot 'gamedata\meshes\anomaly_donation_bridge') -Filter 'stationary_mgun_r*.ogf' |
    Sort-Object Name | Select-Object -ExpandProperty Name)
if (($releaseSpeeds -join ',') -ne 'stationary_mgun_r060.ogf,stationary_mgun_r090.ogf,stationary_mgun_r120.ogf') {
    throw 'Release must contain exactly three speed profiles: 60/90/120.'
}
if ([string]::IsNullOrWhiteSpace($ReadmePath)) { $ReadmePath = Join-Path $projectRoot 'README.md' }
if (-not (Test-Path -LiteralPath $ReadmePath -PathType Leaf)) { throw 'Release README is missing.' }
if (-not [string]::IsNullOrWhiteSpace($ReleaseNotesPath) -and -not (Test-Path -LiteralPath $ReleaseNotesPath -PathType Leaf)) { throw 'Release notes are missing.' }

if (Test-Path -LiteralPath $releaseRoot) {
    Remove-Item -LiteralPath $releaseRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null

Copy-Item -LiteralPath (Join-Path $publishedRoot 'AnomalyDonationBridge.exe') -Destination $stageRoot
Copy-Item -LiteralPath (Join-Path $publishedRoot '_internal') -Destination $stageRoot -Recurse -Force
$stagedService = Join-Path $stageRoot '_internal'
[IO.File]::SetAttributes($stagedService, ([IO.File]::GetAttributes($stagedService) -bor [IO.FileAttributes]::Hidden))
Copy-Item -LiteralPath (Join-Path $publishedRoot 'ADB-APP-001.sha256') -Destination $stagedService
Copy-Item -LiteralPath (Join-Path $publishedRoot 'ADB-APP-001.build.txt') -Destination $stagedService
Copy-Item -LiteralPath (Join-Path $publishedRoot 'addon-package') -Destination $stageRoot -Recurse
Copy-Item -LiteralPath (Join-Path $publishedRoot 'effects') -Destination $stageRoot -Recurse
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination (Join-Path $stageRoot 'LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $projectRoot 'NOTICE.md') -Destination (Join-Path $stagedService 'NOTICE.md')
Copy-Item -LiteralPath (Join-Path $projectRoot 'version.json') -Destination (Join-Path $stagedService 'version.json')
Copy-Item -LiteralPath $ReadmePath -Destination (Join-Path $stageRoot 'README.md')
if (-not [string]::IsNullOrWhiteSpace($ReleaseNotesPath)) {
    $notesVersion = $Version -replace '-.*$',''
    Copy-Item -LiteralPath $ReleaseNotesPath -Destination (Join-Path $stagedService "CHANGELOG_${notesVersion}_RU.md")
}
foreach ($tree in @($stageRoot)) {
    foreach ($item in @(Get-Item -LiteralPath $tree -Force) + @(Get-ChildItem -LiteralPath $tree -Recurse -Force)) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Reparse point is forbidden in the release: $($item.FullName)"
        }
    }
}
$privateNames = @('settings.json','secrets.dpapi','history.jsonl','processed-donations.jsonl','pending-donations.json','pending-queue.json','voting.json','voting.json.bak','voting.json.tmp','bridge.log','crash-recovery.owner')
foreach ($releaseFile in Get-ChildItem -LiteralPath $stageRoot -Recurse -File -Force) {
    if ($releaseFile.Name -in $privateNames -or $releaseFile.Extension -in @('.dmp','.pfx','.p12','.pem','.key','.log') -or
        $releaseFile.Name -eq 'AGENTS.md') { throw "Private/runtime file in release: $($releaseFile.Name)" }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stageRoot, $minimalZip,
    [IO.Compression.CompressionLevel]::Optimal, $false)
$sums = foreach ($zip in @($minimalZip)) {
    "$(Get-FileHash -LiteralPath $zip -Algorithm SHA256 | Select-Object -ExpandProperty Hash) *$([IO.Path]::GetFileName($zip))"
}
[IO.File]::WriteAllText((Join-Path $releaseRoot 'SHA256SUMS.txt'),
    ($sums -join "`n") + "`n", [Text.UTF8Encoding]::new($false))

Write-Host "One-file preset package staged: $minimalZip"
Write-Host ($sums -join "`n")
