param([string]$ExePath)
$ErrorActionPreference = 'Stop'
if (!$ExePath) { $ExePath = Join-Path $PSScriptRoot '..\release\1.3.1.9\LunarisDock-Portable\LunarisDock.exe' }
& "$PSScriptRoot\..\scripts\Invoke-TestProgram.ps1" -SourceFile "$PSScriptRoot\CoreTests.cs" -ExePath $ExePath
& "$PSScriptRoot\..\scripts\Invoke-TestProgram.ps1" -SourceFile "$PSScriptRoot\PreferencesMotionTests.cs" -ExePath $ExePath
& "$PSScriptRoot\..\scripts\Invoke-TestProgram.ps1" -SourceFile "$PSScriptRoot\WindowMotionTests.cs" -ExePath $ExePath
& "$PSScriptRoot\..\scripts\Invoke-TestProgram.ps1" -SourceFile "$PSScriptRoot\LaunchNavigationMotionTests.cs" -ExePath $ExePath
& "$PSScriptRoot\..\scripts\Invoke-TestProgram.ps1" -SourceFile "$PSScriptRoot\DockArtMotionTests.cs" -ExePath $ExePath
& "$PSScriptRoot\..\scripts\Invoke-TestProgram.ps1" -SourceFile "$PSScriptRoot\IconDesignTests.cs" -ExePath $ExePath
& "$PSScriptRoot\..\scripts\Invoke-TestProgram.ps1" -SourceFile "$PSScriptRoot\WirelessParameterTests.cs" -ExePath $ExePath
& "$PSScriptRoot\..\scripts\Invoke-TestProgram.ps1" -SourceFile "$PSScriptRoot\ShortcutPinTests.cs" -ExePath $ExePath
& "$PSScriptRoot\..\scripts\Invoke-TestProgram.ps1" -SourceFile "$PSScriptRoot\ReleaseUpdateTests.cs" -ExePath $ExePath
