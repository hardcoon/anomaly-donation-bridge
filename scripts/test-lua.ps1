[CmdletBinding()]
param(
    [string]$LuaJitPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$scriptRoot = Join-Path $repoRoot 'game-addon\gamedata\scripts'
$legacyRoot = Join-Path $repoRoot 'game-addon\legacy-scripts'
$effectRoot = Join-Path $repoRoot 'effects\gamedata\scripts'
$optionalRoot = Join-Path $repoRoot 'optional-effects\gamedata\scripts'
$archivedChimeraRoot = Join-Path $repoRoot 'deferred-effects\chimera-form\CF-004\gamedata\scripts'
$archivedPsyDogRoot = Join-Path $repoRoot 'deferred-effects\psy-dog-form\PDF-001\gamedata\scripts'
$fixtureRoot = Join-Path $repoRoot 'tests\lua-fixtures'

function Resolve-LuaJitExecutable {
    if (-not [string]::IsNullOrWhiteSpace($LuaJitPath)) {
        $candidate = [IO.Path]::GetFullPath($LuaJitPath)
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
        throw "LuaJIT executable not found: $candidate"
    }

    $preferred = @(
        (Join-Path $repoRoot 'work\luajit\luajit.exe'),
        (Join-Path $repoRoot 'tools\luajit\luajit.exe'),
        (Join-Path $repoRoot '.tools\luajit\luajit.exe')
    )
    foreach ($candidate in $preferred) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }

    $discovered = Get-ChildItem -LiteralPath $repoRoot -Filter 'luajit.exe' -File -Recurse -ErrorAction SilentlyContinue |
        Sort-Object FullName |
        Select-Object -First 1
    if ($null -ne $discovered) { return $discovered.FullName }

    throw 'LuaJIT was not found inside the project. Pass -LuaJitPath <path-to-luajit.exe>.'
}

function Invoke-LuaJit {
    param([Parameter(Mandatory)][string[]]$Arguments)

    & $script:luaJitExe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "LuaJIT exited with code ${LASTEXITCODE}: $($Arguments -join ' ')"
    }
}

if (-not (Test-Path -LiteralPath $scriptRoot -PathType Container)) {
    throw "Addon script directory not found: $scriptRoot"
}
if (-not (Test-Path -LiteralPath $fixtureRoot -PathType Container)) {
    throw "Lua fixture directory not found: $fixtureRoot"
}

$luaJitExe = Resolve-LuaJitExecutable
$scripts = @(Get-ChildItem -LiteralPath $scriptRoot -Filter 'anomaly_donation_bridge_*.script' -File | Sort-Object Name)
$legacyScripts = @(Get-ChildItem -LiteralPath $legacyRoot -Filter 'anomaly_donation_bridge_actions_*.script' -File | Sort-Object Name)
$effects = @(Get-ChildItem -LiteralPath $effectRoot -Filter 'anomaly_donation_bridge_effect_*.script' -File | Sort-Object Name)
$optionalEffects = @(Get-ChildItem -LiteralPath $optionalRoot -Filter 'anomaly_donation_bridge_effect_*.script' -File | Sort-Object Name)
$fixtures = @(Get-ChildItem -LiteralPath $fixtureRoot -Filter '*_smoke.lua' -File | Sort-Object Name)

if ($scripts.Count -ne 28) { throw "Expected 28 installable core scripts, found $($scripts.Count)." }
if ($legacyScripts.Count -ne 3) { throw "Expected three archived regression modules, found $($legacyScripts.Count)." }
if ($effects.Count -ne 4) { throw "Expected four starter effect scripts, found $($effects.Count)." }
if ($optionalEffects.Count -ne 66) { throw "Expected 66 optional effect scripts, found $($optionalEffects.Count)." }
if ($fixtures.Count -ne 53) { throw "Expected 53 Lua smoke fixtures, found $($fixtures.Count)." }

foreach ($script in @($scripts) + @($legacyScripts) + @($effects) + @($optionalEffects)) {
    # Forward slashes and Lua single quotes avoid the MinGW CRT stripping the
    # nested double quotes from `-e` expressions on Windows.
    $escapedPath = $script.FullName.Replace('\', '/').Replace("'", "\'")
    $loadExpression = "assert(loadfile('$escapedPath'))"
    Invoke-LuaJit @('-e', $loadExpression)
}
Write-Host "Lua parse passed: $($scripts.Count) core, $($legacyScripts.Count) legacy regression, $($effects.Count) starter and $($optionalEffects.Count) optional scripts."

# Enforce the user's crossfade default for current AND future shipped forms.
# Particle-only forms (Poltergeist) have no skeletal play_motion calls.
$poseBlendFormCount = 0
$poseBlendCallCount = 0
foreach ($effect in @($effects) + @($optionalEffects)) {
    $source = Get-Content -LiteralPath $effect.FullName -Raw
    if ($source -notmatch '"category"\s*:\s*"metamorphosis"') { continue }
    $poseBlendFormCount++
    $motionCalls = [regex]::Matches($source, ':play_motion\s*\(')
    $blendedCalls = [regex]::Matches($source, ':play_motion\s*\(\s*[^,()\r\n]+,\s*true\s*,\s*1\s*\)')
    if ($motionCalls.Count -ne $blendedCalls.Count) {
        throw "Metamorphosis pose playback must use native mixing=true, speed=1: $($effect.Name). See docs/ADDING_ACTION_RU.md."
    }
    $poseBlendCallCount += $blendedCalls.Count
}
Write-Host "Metamorphosis crossfade contract passed: $poseBlendFormCount forms, $poseBlendCallCount skeletal playback calls; particle-only forms unchanged."

foreach ($fixture in $fixtures) {
    if ($fixture.Name -eq 'roulette_bridge_smoke.lua') {
        Invoke-LuaJit @($fixture.FullName, $scriptRoot, $optionalRoot)
    } elseif ($fixture.Name -in @('metamorphosis_native_contract_smoke.lua', 'form_hit_contract_smoke.lua')) {
        Invoke-LuaJit @($fixture.FullName, $scriptRoot, $effectRoot, $optionalRoot)
    } elseif ($fixture.Name -eq 'chimera_form_smoke.lua') {
        Invoke-LuaJit @($fixture.FullName, $scriptRoot, $effectRoot, $archivedChimeraRoot)
    } elseif ($fixture.Name -eq 'psy_dog_form_smoke.lua') {
        Invoke-LuaJit @($fixture.FullName, $scriptRoot, $effectRoot, $archivedPsyDogRoot)
    } elseif ($fixture.Name -eq 'play_guitar_smoke.lua') {
        Invoke-LuaJit @($fixture.FullName, $scriptRoot, $effectRoot, $optionalRoot)
        Invoke-LuaJit @($fixture.FullName, $scriptRoot, $effectRoot, $optionalRoot, 'legacy-ui')
    } elseif ($fixture.Name -eq 'god_mode_smoke.lua') {
        foreach ($constructorProfile in @('userdata', 'table', 'function')) {
            Invoke-LuaJit @($fixture.FullName, $scriptRoot, $effectRoot, $optionalRoot, $constructorProfile)
        }
    } elseif ($fixture.Name -in @('show_squads_smoke.lua', 'needs_wait_smoke.lua', 'spawn_point_methods_smoke.lua', 'file_effects_smoke.lua', 'actor_boost_smoke.lua', 'new_life_smoke.lua', 'dementia_smoke.lua', 'teleport_level_smoke.lua', 'explosive_effect_smoke.lua',
        'rename_nearest_npc_smoke.lua', 'annoying_fan_smoke.lua', 'cleanup_corpses_smoke.lua', 'mutant_companion_v2_smoke.lua', 'helicopter_boss_smoke.lua', 'helicopter_companion_smoke.lua', 'equipment_ammo_smoke.lua', 'play_guitar_smoke.lua', 'slow_time_smoke.lua', 'aim_teleport_smoke.lua', 'stationary_mgun_smoke.lua', 'burer_form_smoke.lua', 'pseudogiant_form_smoke.lua', 'tushkano_form_smoke.lua', 'poltergeist_form_smoke.lua', 'bloodsucker_form_smoke.lua', 'controller_form_smoke.lua')) {
        Invoke-LuaJit @($fixture.FullName, $scriptRoot, $effectRoot, $optionalRoot)
    } elseif ($fixture.Name -in @('btr_companion_smoke.lua', 'pda_message_smoke.lua', 'special_companion_smoke.lua')) {
        Invoke-LuaJit @($fixture.FullName, $scriptRoot, $effectRoot, $optionalRoot)
        if ($fixture.Name -eq 'btr_companion_smoke.lua') {
            Invoke-LuaJit @($fixture.FullName, $scriptRoot, $effectRoot, $optionalRoot, 'legacy')
        }
    } elseif ($fixture.Name -eq 'donor_boss_smoke.lua') {
        Invoke-LuaJit @($fixture.FullName, $scriptRoot, $optionalRoot)
    } else {
        Invoke-LuaJit @($fixture.FullName, $scriptRoot)
    }
}
foreach ($thiefCase in @('normal', 'actor-moved', 'pending-load', 'legacy-approach',
    'legacy-punch', 'distance-jog', 'default-distance-jog', 'custom-distance-jog',
    'legacy-distance-jog', 'early-death', 'capture-flag-failure', 'capture-kill-failure')) {
    Invoke-LuaJit @((Join-Path $repoRoot 'deferred-effects\donor-thief\native-panic-test\donor_thief_smoke.lua'),
        $scriptRoot, $optionalRoot, $thiefCase)
}
Invoke-LuaJit @((Join-Path $fixtureRoot 'spawn_actions_smoke.lua'),
    $scriptRoot, $effectRoot, $optionalRoot)
Invoke-LuaJit @((Join-Path $fixtureRoot 'inventory_actions_smoke.lua'),
    $scriptRoot, $effectRoot, $optionalRoot)
Invoke-LuaJit @((Join-Path $fixtureRoot 'actor_actions_smoke.lua'),
    $scriptRoot, $optionalRoot)
Write-Host "Lua smoke passed: $($fixtures.Count) legacy/core plus three modular action runs."
Write-Host "Lua checks passed with $luaJitExe."
