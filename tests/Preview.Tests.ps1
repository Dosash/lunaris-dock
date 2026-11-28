[CmdletBinding()]
param(
    [string]$ExePath,
    [string]$OutDir,
    [string]$LaunchName,
    [switch]$StoreCalculator,
    [switch]$StoreOnly,
    [switch]$SettingsOnly,
    [switch]$LibraryOnly,
    [switch]$CaptureFolder
)
$ErrorActionPreference = 'Stop'
if (!$ExePath) { $ExePath = Join-Path $PSScriptRoot '..\release\1.3.1\LunarisDock-Portable\LunarisDock.exe' }
if (!$OutDir) { $OutDir = Join-Path $PSScriptRoot '..\build\test-results' }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$arguments = @((Resolve-Path -LiteralPath $ExePath).Path)
if ($LaunchName) { $arguments += $LaunchName }
if ($StoreCalculator) { $arguments += '--store-calculator' }
if ($StoreOnly) { $arguments += '--store-only' }
if ($SettingsOnly) { $arguments += '--settings-only' }
if ($LibraryOnly) { $arguments += '--library-only' }
if ($CaptureFolder) { $arguments += '--folder-image=' + (Join-Path (Resolve-Path $OutDir).Path 'Start-folder.png') }
$file = if ($StoreOnly) { 'store-ui.txt' } elseif ($LibraryOnly) { 'library-ui.txt' } elseif ($SettingsOnly) { 'settings-ui.txt' } else { 'preview.txt' }
& "$PSScriptRoot\..\scripts\Invoke-TestProgram.ps1" -SourceFile "$PSScriptRoot\PreviewTests.cs" -ExePath $ExePath -TestArguments $arguments 2>&1 |
    Tee-Object -FilePath (Join-Path $OutDir $file)
