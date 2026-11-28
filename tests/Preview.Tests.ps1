[CmdletBinding()]
param(
    [string]$ExePath,
    [string]$OutDir,
    [string]$LaunchName,
    [switch]$StoreCalculator,
    [switch]$StoreOnly,
    [switch]$SettingsOnly,
    [switch]$LibraryOnly,
    [switch]$AppearanceOnly,
    [switch]$StatusOnly,
    [switch]$DevicesOnly,
    [switch]$ExerciseAudio,
    [switch]$ExerciseRadios,
    [switch]$CaptureFolder
)
$ErrorActionPreference = 'Stop'
if (!$ExePath) { $ExePath = Join-Path $PSScriptRoot '..\release\1.3.1.8\LunarisDock-Portable\LunarisDock.exe' }
if (!$OutDir) { $OutDir = Join-Path $PSScriptRoot '..\build\test-results' }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$arguments = @((Resolve-Path -LiteralPath $ExePath).Path)
if ($LaunchName) { $arguments += $LaunchName }
if ($StoreCalculator) { $arguments += '--store-calculator' }
if ($StoreOnly) { $arguments += '--store-only' }
if ($SettingsOnly) { $arguments += '--settings-only' }
if ($LibraryOnly) { $arguments += '--library-only' }
if ($AppearanceOnly) { $arguments += '--appearance-only' }
if ($StatusOnly) { $arguments += '--status-only' }
if ($DevicesOnly) { $arguments += '--devices-only' }
if ($ExerciseAudio) {
    if (!$DevicesOnly) { throw '-ExerciseAudio requires -DevicesOnly.' }
    $arguments += '--exercise-audio'
}
if ($ExerciseRadios) {
    if (!$DevicesOnly) { throw '-ExerciseRadios requires -DevicesOnly.' }
    $arguments += '--exercise-radios'
}
if ($CaptureFolder) { $arguments += '--folder-image=' + (Join-Path (Resolve-Path $OutDir).Path 'Start-folder.png') }
$file = if ($StoreOnly) { 'store-ui.txt' } elseif ($AppearanceOnly) { 'appearance-ui.txt' } elseif ($LibraryOnly) { 'library-ui.txt' } elseif ($SettingsOnly) { 'settings-ui.txt' } else { 'preview.txt' }
if ($StatusOnly) { $file = 'status-ui.txt' }
if ($DevicesOnly) { $file = 'devices-ui.txt' }
$sources = @('PreviewTests','PreviewAppearanceTests','StatusFixture','StatusTrayTests','StatusMenuTests','DevicePreviewTests') | ForEach-Object { Join-Path $PSScriptRoot "$_.cs" }
& "$PSScriptRoot\..\scripts\Invoke-TestProgram.ps1" -SourceFile $sources -ExePath $ExePath -TestArguments $arguments 2>&1 |
    Tee-Object -FilePath (Join-Path $OutDir $file)
