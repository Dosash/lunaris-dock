[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExpectedUserSid,
    [string]$ExePath,
    [switch]$RestartExplorer,
    [switch]$NativeStart
)
$ErrorActionPreference = 'Stop'
if (!$ExePath) { $ExePath = Join-Path $PSScriptRoot '..\release\1.3.1.6\LunarisDock-Portable\LunarisDock.exe' }
. "$PSScriptRoot\Lab.ps1"
Assert-LabAccount $ExpectedUserSid
$exe = (Resolve-Path -LiteralPath $ExePath).Path
$session = (Get-Process -Id $PID).SessionId
if (Get-Process -Name LunarisDock -ErrorAction SilentlyContinue | Where-Object SessionId -eq $session) { throw 'Another Dock is active in the test session.' }
$previousProfile = $env:LUNARIS_PROFILE
$env:LUNARIS_PROFILE = Join-Path $env:TEMP ('Lunaris-Shell-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $env:LUNARIS_PROFILE | Out-Null
[IO.File]::WriteAllText((Join-Path $env:LUNARIS_PROFILE 'settings.json'), '{"ReplaceTaskbar":true,"InitializePins":true,"Size":48,"Magnify":true}')
$state = [LunarisLab]::State()
$visible = [LunarisLab]::BarVisible()
if (-not $visible) { throw 'Cannot establish a visible native taskbar baseline.' }
$cursor = New-Object LunarisLab+Pt
[void][LunarisLab]::GetCursorPos([ref]$cursor)
function Send-Chord([byte[]]$Keys) {
    try { foreach ($k in $Keys) { [LunarisLab]::Key($k,$false); Start-Sleep -Milliseconds 45 } }
    finally { for ($i=$Keys.Length-1; $i -ge 0; $i--) { [LunarisLab]::Key($Keys[$i],$true) } }
}
function Start-ShellTest {
    $d = Start-Process $exe -PassThru
    Wait-Observed { (Get-LabWindow 'Dock' $d) -ne [IntPtr]::Zero -and ![LunarisLab]::BarVisible() } 'replacement Dock appeared and hid Explorer taskbar'
    return $d
}
function Assert-Restored {
    Wait-Observed { ([LunarisLab]::BarVisible() -eq $visible) -and ([LunarisLab]::State() -eq $state) } 'native taskbar visibility and appbar flags restored'
}
function Test-NativeStartForeground {
    $owner = [LunarisLab]::Owner([LunarisLab]::GetForegroundWindow())
    return (Get-Process -Id $owner -ErrorAction SilentlyContinue).ProcessName -eq 'StartMenuExperienceHost'
}
$dock = $null
try {
    $dock = Start-ShellTest
    foreach ($vk in 0x5b,0x5c) {
        Send-Chord @($vk)
        Wait-Observed { (Get-LabWindow 'Launchpad' $dock) -ne [IntPtr]::Zero } "single Win $vk opens Launchpad"
        Send-Chord @($vk)
        Wait-Observed { (Get-LabWindow 'Launchpad' $dock) -eq [IntPtr]::Zero } "repeated Win $vk dismisses Launchpad"
    }
    if ($NativeStart) {
        # Ctrl+Esc is a native Start gesture independent of either bare Windows key.
        Send-Chord @(0x11,0x1b)
        Wait-Observed { Test-NativeStartForeground } 'with redirection off Ctrl+Esc activates the Windows Start host'
        Send-Chord @(0x1b)
        Wait-Observed { !(Test-NativeStartForeground) } 'native Start dismissed'
        $prefs = Open-LabSettings $exe $dock
        Set-LabToggle $prefs 'HideWindowsStart' $true
        Close-LabSettings $prefs $dock
        Send-Chord @(0x11,0x1b)
        Wait-Observed {
            (Get-LabWindow 'Launchpad' $dock) -ne [IntPtr]::Zero -and
            [LunarisLab]::Owner([LunarisLab]::GetForegroundWindow()) -eq $dock.Id
        } 'enabling native Start redirection routes Ctrl+Esc to the foreground Dock Launchpad'
        $prefs = Open-LabSettings $exe $dock
        Set-LabToggle $prefs 'HideWindowsStart' $false
        Close-LabSettings $prefs $dock
        Send-Chord @(0x11,0x1b)
        Wait-Observed { (Test-NativeStartForeground) -and (Get-LabWindow 'Launchpad' $dock) -eq [IntPtr]::Zero } 'disabling redirection restores Ctrl+Esc to native Start'
        Send-Chord @(0x1b)
    }
    Send-Chord @(0x5b,0x52)
    Wait-Observed { $h=[LunarisLab]::GetForegroundWindow(); $owner=[LunarisLab]::Owner($h); $p=Get-Process -Id $owner -ErrorAction SilentlyContinue; $class=New-Object Text.StringBuilder 128; [void][LunarisLab]::GetClassName($h,$class,128); $p.ProcessName -eq 'explorer' -and $class.ToString() -eq '#32770' } 'Win+R opens the native Run dialog'
    Send-Chord @(0x1b)
    [void][LunarisLab]::SetCursorPos([int]([LunarisLab]::GetSystemMetrics(0)/2),([LunarisLab]::GetSystemMetrics(1)-1))
    for ($i=0; $i -lt 20; $i++) { if ([LunarisLab]::BarVisible()) { throw 'Explorer taskbar reappeared at bottom edge.' }; Start-Sleep -Milliseconds 100 }
    Write-Host 'PASS pointer at bottom edge for 2s'
    if ($RestartExplorer) {
        $shellId = [LunarisLab]::Owner([LunarisLab]::Bar())
        $shell = Get-Process -Id $shellId
        if ($shell.ProcessName -ne 'explorer' -or $shell.SessionId -ne $session) { throw 'Cannot identify target test Explorer.' }
        $shell.Kill()
        if (!$shell.WaitForExit(8000)) { throw 'Test Explorer did not stop.' }
        Start-Sleep -Seconds 4
        if ([LunarisLab]::Bar() -eq [IntPtr]::Zero) { Start-Process (Join-Path $env:windir 'explorer.exe') -WindowStyle Hidden }
        Wait-Observed { $h=[LunarisLab]::Bar(); $h -ne [IntPtr]::Zero -and [LunarisLab]::Owner($h) -ne $shellId -and ![LunarisLab]::BarVisible() } 'restarted Explorer is governed by the running Dock' 25
    }
    $client = Start-Process $exe -ArgumentList '--exit' -PassThru -Wait -WindowStyle Hidden
    if ($client.ExitCode -ne 0 -or !$dock.WaitForExit(4000)) { throw 'Graceful shutdown failed.' }
    Assert-Restored
    $dock = Start-ShellTest
    $dock.Kill()
    if (!$dock.WaitForExit(8000)) { throw 'Test Dock did not stop.' }
    Assert-Restored
    Send-Chord @(0x5b)
    Wait-Observed { $owner=[LunarisLab]::Owner([LunarisLab]::GetForegroundWindow()); (Get-Process -Id $owner -ErrorAction SilentlyContinue).ProcessName -eq 'StartMenuExperienceHost' } 'native Start after Dock terminates'
    Send-Chord @(0x1b)
} finally {
    if ($dock -and !$dock.HasExited) { Start-Process $exe -ArgumentList '--exit' -Wait -WindowStyle Hidden }
    if ([LunarisLab]::Bar() -eq [IntPtr]::Zero) { Start-Process (Join-Path $env:windir 'explorer.exe') -WindowStyle Hidden; Start-Sleep -Seconds 2 }
    Start-Process $exe -ArgumentList '--recover' -Wait -WindowStyle Hidden
    [void][LunarisLab]::SetCursorPos($cursor.X,$cursor.Y)
    $env:LUNARIS_PROFILE=$previousProfile
}
