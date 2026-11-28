[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExpectedUserSid,
    [string]$Installer = (Join-Path $PSScriptRoot '..\release\1.3.1.9\LunarisDock-1.3.1.9-x64-Setup.exe'),
    [string]$PreviousInstaller
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Lab.ps1"
Assert-LabAccount $ExpectedUserSid
if ($env:LUNARIS_PROFILE) { throw 'Use the default profile in a clean test account.' }
$dir = Join-Path $env:LOCALAPPDATA 'Programs\Lunaris Dock'
$profile = Join-Path $env:LOCALAPPDATA 'LunarisDock\Profile'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Lunaris Dock.lnk'
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Lunaris Dock.lnk'
$exe = Join-Path $dir 'LunarisDock.exe'
$uninst = Join-Path $dir 'unins000.exe'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$languageKey = 'HKCU:\Software\LunarisDock'
$uninstKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{667F8C56-9E92-4C1A-8FA3-18D59F1A575D}_is1'
if ((Test-Path $dir) -or (Test-Path $profile) -or (Test-Path $shortcut) -or (Test-Path $desktopShortcut) -or (Test-Path $uninstKey) -or
    (Get-ItemProperty $languageKey -Name InstallLanguage -ErrorAction SilentlyContinue) -or
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
    $firstLanguage = if ($PreviousInstaller) { 'ru' } else { 'de' }
    Invoke-Installer $first (Join-Path $logDir 'first.log') $firstLanguage
    Wait-Observed { (Test-Path $exe) -and (Test-Path $uninst) -and (Test-Path $shortcut) -and (Test-Path $uninstKey) } 'per-user executable, uninstaller, Start shortcut and uninstall registration'
    $oldVersion = [version](Get-ItemPropertyValue $uninstKey DisplayVersion)
    if (Get-ItemProperty $runKey -Name LunarisDock -ErrorAction SilentlyContinue) { throw 'Clean setup enabled startup by default.' }
    $dock = Start-Process $exe -PassThru
    Wait-Observed { Test-Path (Join-Path $profile 'settings.json') } 'installed executable generated real initial pins'
    $cfg = Get-Content -LiteralPath (Join-Path $profile 'settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $pins = @($cfg.Pins)
    if ($pins.Count -lt 1) { throw 'No default pins in test account.' }
    $expectedLanguage = 'zh-CN'
    if (!$PreviousInstaller -or $cfg.Language) {
        if ($cfg.Language -ne $firstLanguage) { throw 'First profile did not inherit the installer language.' }
        $prefs = Open-LabSettings $exe $dock
        Set-LabToggle $prefs 'Startup' $true
        $combo = Get-LabSetting $prefs 'Language'
        $combo.SetFocus()
        $dropdown = $combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
        $dropdown.Expand()
        (New-Object -ComObject WScript.Shell).SendKeys('{HOME}{DOWN 3}{ENTER}')
        Wait-Observed {
            $saved = Get-Content -LiteralPath (Join-Path $profile 'settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
            $saved.Language -eq 'es'
        } 'Spanish language selected in the installed app'
        Close-LabSettings $prefs $dock
        $expectedLanguage = 'es'
    }
    Invoke-Installer $Installer (Join-Path $logDir 'upgrade.log') 'zhCN'
    if ((Get-ItemPropertyValue $languageKey InstallLanguage) -ne 'zhCN') { throw 'Setup did not record the new installation language.' }
    if ($expectedLanguage -eq 'es' -and (Get-ItemPropertyValue $runKey LunarisDock) -ne ('"' + $exe + '"')) {
        throw 'Upgrade lost the startup preference set in the app.'
    }
    if (!(Test-Path -LiteralPath $desktopShortcut) -or
        (New-Object -ComObject WScript.Shell).CreateShortcut($desktopShortcut).TargetPath -ne $exe) {
        throw 'Missing per-user desktop shortcut to the installed executable.'
    }
    Wait-Observed { $dock.HasExited } 'updater gracefully closed the running installed Dock'
    $newVersion = [version](Get-ItemPropertyValue $uninstKey DisplayVersion)
    if ($PreviousInstaller -and $newVersion -le $oldVersion) { throw 'Previous installer did not contain a lower version.' }
    $cfg = Get-Content -LiteralPath (Join-Path $profile 'settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if (($cfg.Pins -join '|') -cne ($pins -join '|')) { throw 'Pin order changed during installation.' }
    if ($PreviousInstaller) { Write-Host "PASS upgrade $oldVersion -> $newVersion with unchanged pins" }
    else { Write-Host "PASS reinstall $newVersion with unchanged pins" }
    Invoke-Installer $Installer (Join-Path $logDir 'startup-on.log') 'zhCN' 'startup'
    if ((Get-ItemPropertyValue $runKey LunarisDock) -ne ('"' + $exe + '"')) { throw 'Setup failed to enable startup without launching the app.' }
    Invoke-Installer $Installer (Join-Path $logDir 'startup-off.log') 'zhCN' '!startup'
    if (Get-ItemProperty $runKey -Name LunarisDock -ErrorAction SilentlyContinue) { throw 'Setup failed to disable startup without launching the app.' }
    $dock = Start-Process $exe -PassThru
    Wait-Observed { (Get-LabWindow 'Dock' $dock) -ne [IntPtr]::Zero } 'updated installed Dock appears'
    $prefs = Open-LabSettings $exe $dock
    $startup = (Get-LabSetting $prefs 'Startup').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    if ($startup.Current.ToggleState -ne [Windows.Automation.ToggleState]::Off) { throw 'Profile overrode the last setup choice.' }
    Close-LabSettings $prefs $dock
    Set-InstalledShellOptions $exe $dock
    $cfg = Get-Content -LiteralPath (Join-Path $profile 'settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($cfg.Language -ne $expectedLanguage) { throw "Expected profile language $expectedLanguage after upgrade, got $($cfg.Language)." }
    Write-Host "PASS profile language after installation and upgrade: $expectedLanguage"
    Wait-Observed { -not [LunarisLab]::BarVisible() -and (Get-ItemProperty $runKey -Name LunarisDock -ErrorAction SilentlyContinue) } 'installed build owns taskbar and autostart'
    Invoke-Installer $uninst (Join-Path $logDir 'remove.log')
    Wait-Observed { $dock.HasExited -and !(Test-Path $exe) -and !(Test-Path $shortcut) -and !(Test-Path $desktopShortcut) -and !(Test-Path $uninstKey) } 'running Dock and both shortcuts uninstalled'
    if (Get-ItemProperty $languageKey -Name InstallLanguage -ErrorAction SilentlyContinue) { throw 'Uninstall left the language registration behind.' }
    Wait-Observed { ([LunarisLab]::State() -eq $state) -and ([LunarisLab]::BarVisible() -eq $visible) -and !(Get-ItemProperty $runKey -Name LunarisDock -ErrorAction SilentlyContinue) } 'Explorer taskbar state restored and autostart removed'
} finally {
    if ($dock -and !$dock.HasExited -and (Test-Path $exe)) {
        Start-Process $exe -ArgumentList '--exit' -Wait -WindowStyle Hidden
    }
    if (Test-Path $uninst) { Invoke-Installer $uninst (Join-Path $logDir 'cleanup.log') }
}
