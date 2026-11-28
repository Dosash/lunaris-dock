[CmdletBinding()]
param(
    [int]$DockProcessId,
    [switch]$AllowLegacyStart,
    [ValidateSet('All','Tray','Menus','Sound','Network','Language','Start','Notifications')]
    [string]$Scenario='All',
    [switch]$RequireMixedDpi,
    [switch]$CompileOnly,
    [string]$ReferenceExe = (Join-Path $PSScriptRoot '..\build\LunarisDock.exe')
)
$ErrorActionPreference = 'Stop'
$arguments = @()
if (!$CompileOnly) {
    if ($DockProcessId -le 0) { throw 'Pass the process ID of the running normal Dock instance.' }
    $owner = Get-CimInstance Win32_Process -Filter "ProcessId=$DockProcessId"
    if (!$owner -or $owner.Name -ne 'LunarisDock.exe' -or $owner.SessionId -ne (Get-Process -Id $PID).SessionId -or
        !$owner.ExecutablePath -or $owner.CommandLine -match '(?i)--(preview|guardian)\b') {
        throw 'Expected a normal LunarisDock.exe in this interactive session.'
    }
    $guard = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$DockProcessId" | Where-Object {
        $_.ExecutablePath -eq $owner.ExecutablePath -and $_.SessionId -eq $owner.SessionId -and
        $_.CommandLine -match ("(?i)--guardian\s+" + $DockProcessId + "\s+")
    })
    if ($guard.Count -ne 1) { throw 'The specified Dock has no unique active replacement-mode guardian.' }
    if ((Get-FileHash -LiteralPath $ReferenceExe).Hash -ne (Get-FileHash -LiteralPath $owner.ExecutablePath).Hash) {
        throw 'ReferenceExe differs from the running Dock binary.'
    }
    $canonical = @('All','Tray','Menus','Sound','Network','Language','Start','Notifications') -eq $Scenario
    $arguments = @([string]$DockProcessId, [string]$guard[0].ProcessId, $canonical[0])
    if ($RequireMixedDpi) { $arguments += '--require-mixed-dpi' }
    if ($AllowLegacyStart) { $arguments += '--allow-legacy-start' }
}
$sources = @('PreviewTests','PreviewAppearanceTests','StatusFixture','StatusTrayTests','StatusMenuTests','DevicePreviewTests','LiveStatusBarTests') | ForEach-Object { Join-Path $PSScriptRoot "$_.cs" }
& "$PSScriptRoot\..\scripts\Invoke-TestProgram.ps1" -SourceFile $sources -MainType LiveStatusBarTests -ExePath $ReferenceExe -TestArguments $arguments -CompileOnly:$CompileOnly
