[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExpectedUserSid,
    [string]$Installer = (Join-Path $PSScriptRoot '..\release\1.3.0\LunarisDock-1.3.0-x64-Setup.exe'),
    [string]$PreviousInstaller
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Lab.ps1"
Assert-LabAccount $ExpectedUserSid
if ($env:LUNARIS_PROFILE) { throw 'Use the default profile in a clean test account.' }
$dir = Join-Path $env:LOCALAPPDATA 'Programs\Lunaris Dock'
$profile = Join-Path $env:LOCALAPPDATA 'LunarisDock\Profile'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Lunaris Dock.lnk'
$exe = Join-Path $dir 'LunarisDock.exe'
$uninst = Join-Path $dir 'unins000.exe'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$uninstKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{667F8C56-9E92-4C1A-8FA3-18D59F1A575D}_is1'
if ((Test-Path $dir) -or (Test-Path $profile) -or (Test-Path $shortcut) -or (Test-Path $uninstKey) -or
    (Get-ItemProperty $runKey -Name LunarisDock -ErrorAction SilentlyContinue)) {
    throw 'This account already has a Lunaris installation or profile.'
}
$logDir = Join-Path $env:TEMP ('Lunaris-Install-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $logDir | Out-Null
Write-Host "Logs: $logDir"
$state = [LunarisLab]::State()
$visible = [LunarisLab]::BarVisible()
$first = $Installer
if ($PreviousInstaller) { $first = $PreviousInstaller }
$dock = $null
try {
    Invoke-Installer $first (Join-Path $logDir 'first.log')
    Wait-Observed { (Test-Path $exe) -and (Test-Path $uninst) -and (Test-Path $shortcut) -and (Test-Path $uninstKey) } 'per-user executable, uninstaller, Start shortcut and uninstall registration'
    $oldVersion = [version](Get-ItemPropertyValue $uninstKey DisplayVersion)
    $dock = Start-Process $exe -PassThru
    Wait-Observed { Test-Path (Join-Path $profile 'settings.json') } 'installed executable generated real initial pins'
    $cfg = Get-Content -LiteralPath (Join-Path $profile 'settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $pins = @($cfg.Pins)
    if ($pins.Count -lt 1) { throw 'No default pins in test account.' }
    Invoke-Installer $Installer (Join-Path $logDir 'upgrade.log')
    Wait-Observed { $dock.HasExited } 'updater gracefully closed the running installed Dock'
    $newVersion = [version](Get-ItemPropertyValue $uninstKey DisplayVersion)
    if ($PreviousInstaller -and $newVersion -le $oldVersion) { throw 'Previous installer did not contain a lower version.' }
    $cfg = Get-Content -LiteralPath (Join-Path $profile 'settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if (($cfg.Pins -join '|') -cne ($pins -join '|')) { throw 'Pin order changed during installation.' }
    if ($PreviousInstaller) { Write-Host "PASS upgrade $oldVersion -> $newVersion with unchanged pins" }
    else { Write-Host "PASS reinstall $newVersion with unchanged pins" }
    $dock = Start-Process $exe -PassThru
    Wait-Observed { (Get-LabWindow 'Lunaris Dock' $dock) -ne [IntPtr]::Zero } 'updated installed Dock appears'
    Set-InstalledShellOptions $exe $dock
    Wait-Observed { -not [LunarisLab]::BarVisible() -and (Get-ItemProperty $runKey -Name LunarisDock -ErrorAction SilentlyContinue) } 'installed build owns taskbar and autostart'
    Invoke-Installer $uninst (Join-Path $logDir 'remove.log')
    Wait-Observed { $dock.HasExited -and !(Test-Path $exe) -and !(Test-Path $shortcut) -and !(Test-Path $uninstKey) } 'running Dock uninstalled'
    Wait-Observed { ([LunarisLab]::State() -eq $state) -and ([LunarisLab]::BarVisible() -eq $visible) -and !(Get-ItemProperty $runKey -Name LunarisDock -ErrorAction SilentlyContinue) } 'Explorer taskbar state restored and autostart removed'
} finally {
    if ($dock -and !$dock.HasExited -and (Test-Path $exe)) {
        Start-Process $exe -ArgumentList '--exit' -Wait -WindowStyle Hidden
    }
    if (Test-Path $uninst) { Invoke-Installer $uninst (Join-Path $logDir 'cleanup.log') }
}
