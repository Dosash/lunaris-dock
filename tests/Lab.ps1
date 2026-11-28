# Common probes for a dedicated Windows 11 test account or VM.
Add-Type -AssemblyName UIAutomationClient

function Assert-LabAccount([string]$ExpectedUserSid) {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    if ($ExpectedUserSid -ne $sid) { throw "The specified disposable-account SID does not match this session ($sid)." }
    if (!(Get-Process explorer -ErrorAction SilentlyContinue | Where-Object SessionId -eq (Get-Process -Id $PID).SessionId)) {
        throw 'The test needs an interactive Explorer session.'
    }
}

if (-not ('LunarisLab' -as [type])) {
    Add-Type @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
public static class LunarisLab {
    [StructLayout(LayoutKind.Sequential)] public struct AppBar {
        public UInt32 cbSize; public IntPtr hWnd; public UInt32 callback, edge;
        public Int32 l,t,r,b; public IntPtr param;
    }
    [StructLayout(LayoutKind.Sequential)] public struct Pt { public int X,Y; }
    [DllImport("shell32.dll")] public static extern UIntPtr SHAppBarMessage(uint msg, ref AppBar data);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string cls,string title);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out int id);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Pt pt);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int metric);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk,byte scan,uint flags,UIntPtr extra);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder b,int n);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    public static int Owner(IntPtr h) { int id; GetWindowThreadProcessId(h,out id); return id; }
    public static ulong State() { var a=new AppBar(); a.cbSize=(uint)Marshal.SizeOf(typeof(AppBar)); return SHAppBarMessage(4, ref a).ToUInt64(); }
    public static IntPtr Bar() { return FindWindow("Shell_TrayWnd",null); }
    public static bool BarVisible() { return Bar()!=IntPtr.Zero && IsWindowVisible(Bar()); }
    public static void Key(byte key,bool up) { keybd_event(key,0,up?2u:0u,UIntPtr.Zero); }
}
'@
}
function Wait-Observed([scriptblock]$Condition, [string]$Label, [int]$Seconds=15) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        if (& $Condition) { Write-Host "PASS $Label"; return }
        Start-Sleep -Milliseconds 100
    } while ($clock.Elapsed.TotalSeconds -lt $Seconds)
    throw "Failed: $Label"
}
function Invoke-Installer([string]$File, [string]$Log, [string]$Language, [string]$Tasks) {
    $options = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER', ('/LOG="' + $Log + '"'))
    if ($Language) { $options += "/LANG=$Language" }
    if ($Tasks) { $options += "/MERGETASKS=$Tasks" }
    $proc = Start-Process -FilePath $File -ArgumentList $options -WindowStyle Hidden -Wait -PassThru
    if ($proc.ExitCode -ne 0) { throw "Setup exited with $($proc.ExitCode). See $Log" }
}

function Get-LabWindow([string]$Id, [Diagnostics.Process]$Dock) {
    $processMatch = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $Dock.Id)
    $idMatch = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $query = [Windows.Automation.AndCondition]::new($processMatch, $idMatch)
    $window = [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children, $query)
    if ($window -and !$window.Current.IsOffscreen) { return [IntPtr]$window.Current.NativeWindowHandle }
    return [IntPtr]::Zero
}

function Open-LabSettings([string]$Exe, [Diagnostics.Process]$Dock) {
    if ((Get-LabWindow 'Launchpad' $Dock) -eq [IntPtr]::Zero) {
        Start-Process $Exe -ArgumentList '--toggle' -Wait -WindowStyle Hidden
    }
    Wait-Observed { [LunarisLab]::Owner([LunarisLab]::GetForegroundWindow()) -eq $Dock.Id -and (Get-LabWindow 'Launchpad' $Dock) -ne [IntPtr]::Zero } 'installed Launchpad has keyboard focus'
    (New-Object -ComObject WScript.Shell).SendKeys('^,')
    Wait-Observed { (Get-LabWindow 'Settings' $Dock) -ne [IntPtr]::Zero } 'settings opened by keyboard'
    return [Windows.Automation.AutomationElement]::FromHandle((Get-LabWindow 'Settings' $Dock))
}
function Get-LabSetting($Root, [string]$Id) {
    $match = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $item = $Root.FindFirst([Windows.Automation.TreeScope]::Descendants, $match)
    if (!$item) { throw "Settings control unavailable: $Id" }
    return $item
}
function Set-LabToggle($Root, [string]$Id, [bool]$Enabled) {
    $toggle = (Get-LabSetting $Root $Id).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    if (($toggle.Current.ToggleState -eq [Windows.Automation.ToggleState]::On) -ne $Enabled) { $toggle.Toggle() }
}
function Close-LabSettings($Root, [Diagnostics.Process]$Dock) {
    (Get-LabSetting $Root 'CloseSettings').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Observed { (Get-LabWindow 'Settings' $Dock) -eq [IntPtr]::Zero } 'settings closed after live changes'
}
function Set-InstalledShellOptions([string]$Exe, [Diagnostics.Process]$Dock) {
    $root = Open-LabSettings $Exe $Dock
    Set-LabToggle $root 'ReplaceTaskbar' $true
    Set-LabToggle $root 'Startup' $true
    Close-LabSettings $root $Dock
}
