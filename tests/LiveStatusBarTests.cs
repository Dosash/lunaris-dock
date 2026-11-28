using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Forms;
using AE = System.Windows.Automation.AutomationElement;

// Attaches to the specified Dock. The synthetic fixture runs in a separate test process.
internal static class LiveStatusBarTests
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if(PreviewTests.RunStatusFixture(args)) return 0;
            PreviewTests.AttachToStatusBar(Int32.Parse(args[0]),Int32.Parse(args[1]),args[2],args.Contains("--allow-legacy-start"),args.Contains("--require-mixed-dpi"));
            return 0;
        }
        catch(Exception ex) { Console.WriteLine("FAIL "+ex); return 1; }
    }
}

internal static partial class PreviewTests
{
    [StructLayout(LayoutKind.Sequential)]
    struct MonitorData { public int Size; public NativeRect Monitor,Work; public uint Flags; }
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorData data);
    [StructLayout(LayoutKind.Sequential)]
    struct AppBarData { public int Size; public IntPtr Window; public uint Callback,Edge; public NativeRect Rect; public IntPtr Parameter; }
    [DllImport("shell32.dll")] static extern UIntPtr SHAppBarMessage(uint msg,ref AppBarData data);
    [DllImport("dwmapi.dll",EntryPoint="DwmGetWindowAttribute")] static extern int FrameBounds(IntPtr hwnd,int attribute,out NativeRect bounds,int size);

    static void CheckMaximize()
    {
        foreach(var screen in Screen.AllScreens)
        using(var form=new Form { StartPosition=FormStartPosition.Manual, ShowInTaskbar=false,
            Bounds=new System.Drawing.Rectangle(screen.Bounds.Left+40,screen.Bounds.Top+70,320,200) })
        {
            form.Show();
            var monitor=MonitorFromWindow(form.Handle,0);
            form.WindowState=FormWindowState.Maximized;
            Until(delegate {
                Application.DoEvents();
                var data=new MonitorData { Size=Marshal.SizeOf(typeof(MonitorData)) };
                NativeRect frame;
                return IsZoomed(form.Handle) && MonitorFromWindow(form.Handle,0)==monitor && GetMonitorInfo(monitor,ref data) &&
                    data.Monitor.Left==screen.Bounds.Left && data.Monitor.Top==screen.Bounds.Top &&
                    data.Work.Bottom==data.Monitor.Bottom && FrameBounds(form.Handle,9,out frame,16)==0 &&
                    frame.Bottom==data.Work.Bottom && frame.Top==data.Work.Top;
            },"maximized window reaches the physical bottom and respects the work-area top on "+screen.DeviceName);
        }
    }

    static void CheckExpandedAppBar(AE bar,IntPtr shell)
    {
        // The full-replacement host stays registered while the strip is collapsed.
        // Resolve the stock reference directly by Explorer PID, without going
        // through SHAppBarMessage or depending on the shell window's Z order.
        NativeRect reference;
        if(shell!=ExplorerTaskbar() || Owner(shell)!=Owner(GetShellWindow()) || !GetWindowRect(shell,out reference))
            throw new Exception("Cannot read the real Explorer taskbar HWND rectangle.");
        if(Owner(FindWindow("Shell_TrayWnd",null))!=dock.Id)
            throw new Exception("The expanded tray host is not active for the shell geometry query.");
        var data=new AppBarData { Size=Marshal.SizeOf(typeof(AppBarData)) };
        if(SHAppBarMessage(5,ref data)==UIntPtr.Zero || data.Edge>3)
            throw new Exception("ABM_GETTASKBARPOS failed or returned an invalid taskbar edge.");
        var r=data.Rect;
        int width=r.Right-r.Left, height=r.Bottom-r.Top;
        var monitor=Screen.FromHandle(shell).Bounds;
        bool horizontal=data.Edge==1 || data.Edge==3;
        int gap=data.Edge==0 ? r.Left-monitor.Left : data.Edge==1 ? r.Top-monitor.Top :
            data.Edge==2 ? r.Right-monitor.Right : r.Bottom-monitor.Bottom;
        if(width<=0 || height<=0 ||
            (horizontal ? width<monitor.Width/2 || height>monitor.Height/3 : height<monitor.Height/2 || width>monitor.Width/3) ||
            Math.Abs(gap)>2 ||
            !monitor.IntersectsWith(System.Drawing.Rectangle.FromLTRB(r.Left,r.Top,r.Right,r.Bottom)))
            throw new Exception(String.Format("ABM_GETTASKBARPOS returned implausible edge {0} bounds {1},{2}..{3},{4} for monitor {5}.",
                data.Edge,r.Left,r.Top,r.Right,r.Bottom,monitor));
        if(Math.Abs(r.Left-reference.Left)>2 || Math.Abs(r.Right-reference.Right)>2 ||
            Math.Abs(r.Top-reference.Top)>2 || Math.Abs(r.Bottom-reference.Bottom)>2)
            throw new Exception(String.Format("ABM_GETTASKBARPOS {0},{1}..{2},{3} disagrees with Explorer HWND {4},{5}..{6},{7}.",
                r.Left,r.Top,r.Right,r.Bottom,reference.Left,reference.Top,reference.Right,reference.Bottom));
        CheckAppBar(bar);
        Console.WriteLine("PASS external ABM_GETTASKBARPOS returned the Explorer baseline while the per-monitor upper strip was expanded");
    }

    [StructLayout(LayoutKind.Explicit,Size=40)]
    struct KeyboardInput {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(12)] public uint Flags;
    }
    [DllImport("user32.dll")] static extern uint SendInput(uint count,KeyboardInput[] data,int size);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);

    static void CheckStartEntries(AE statusBar)
    {
        if(Window("Launchpad")!=null) throw new Exception("Start must be closed before testing its entry points.");
        var screen=Screen.FromHandle(new IntPtr(statusBar.Current.NativeWindowHandle));
        try
        {
            var lower=Reveal(screen);
            ClickControl(lower,"OpenLaunchpad");
            WaitFor(()=>Window("Launchpad"),"lower Dock Start button opens Launchpad in the selected process");
            Keys("{ESC}");
            Until(()=>Window("Launchpad")==null,"closed Start after the lower Dock button");

            if(new[]{0x10,0x11,0x12,0x5B,0x5C}.Any(vk=>GetAsyncKeyState(vk)<0))
                throw new Exception("A modifier is held during the bare Win key check.");
            var pair=new[]{new KeyboardInput { Type=1,Key=0x5B,Flags=1 },
                new KeyboardInput { Type=1,Key=0x5B,Flags=3 }};
            if(SendInput(2,pair,Marshal.SizeOf(typeof(KeyboardInput)))!=2)
                throw new Exception("Windows did not receive the bare Win key down/up pair.");
            WaitFor(()=>Window("Launchpad"),"bare Win opens Launchpad in the selected process");
            Keys("{ESC}");
            Until(()=>Window("Launchpad")==null,"closed Start after the bare Win key");
        }
        finally
        {
            var pad=Window("Launchpad");
            if(pad!=null) {
                var hwnd=new IntPtr(pad.Current.NativeWindowHandle);
                if(Owner(hwnd)==dock.Id) {
                    PostMessage(hwnd,0x0100,new IntPtr(0x1B),IntPtr.Zero);
                    PostMessage(hwnd,0x0101,new IntPtr(0x1B),IntPtr.Zero);
                }
            } else {
                var active=GetForegroundWindow();
                int activePid=Owner(active);
                if(activePid!=0) {
                    try {
                        using(var process=Process.GetProcessById(activePid))
                            if(process.ProcessName=="StartMenuExperienceHost" && GetForegroundWindow()==active) SendKeys.SendWait("{ESC}");
                    } catch(ArgumentException) { }
                }
            }
        }
    }

    static bool NotificationCenter(IntPtr hwnd)
    {
        if(!ShellFlyout(hwnd)) return false;
        var flyout=AE.FromHandle(hwnd);
        // Only inspect the shell window title and structural AutomationIds.
        // The names/values of notifications and calendar entries stay private.
        string title=flyout.Current.Name;
        if(new[]{"Notification Center","Notification Centre","Центр уведомлений","Benachrichtigungscenter",
            "Centro de notificaciones","通知中心"}.Contains(title,StringComparer.OrdinalIgnoreCase)) return true;
        string[] semanticIds={"NotificationCenter","NotificationList","NotificationView","ActionCenter",
            "CalendarView","CalendarPanel","CalendarControl","CalendarGrid","CalendarDay","CalendarContainer"};
        return flyout.FindAll(Desc,Condition.TrueCondition).Cast<AE>().Any(e=>{
            string id=e.Current.AutomationId??"";
            return semanticIds.Any(s=>id.IndexOf(s,StringComparison.OrdinalIgnoreCase)>=0) ||
                e.Current.ControlType==ControlType.Calendar;
        });
    }

    static void CheckLiveNotifications(AE bar)
    {
        IntPtr opened=IntPtr.Zero;
        try
        {
            ClickControl(bar,"StatusBar.Notifications");
            Until(delegate {
                Application.DoEvents();
                IntPtr active=GetForegroundWindow();
                if(!ShellFlyout(active)) return false;
                opened=active;
                return NotificationCenter(active);
            },"the real Windows notification center opens from the status bar while TaskbarGuard is active");
        }
        finally
        {
            if(opened!=IntPtr.Zero && GetForegroundWindow()==opened) SendKeys.SendWait("{ESC}");
            if(opened!=IntPtr.Zero)
                Until(()=>GetForegroundWindow()!=opened || !IsWindowVisible(opened),"closed the test-opened Windows notification center");
        }
    }

    static void CheckAppBar(AE bar)
    {
        var hwnd=new IntPtr(bar.Current.NativeWindowHandle);
        var data=new MonitorData { Size=Marshal.SizeOf(typeof(MonitorData)) };
        NativeRect b;
        if(Owner(hwnd)!=dock.Id || !GetWindowRect(hwnd,out b) || !GetMonitorInfo(MonitorFromWindow(hwnd,2),ref data))
            throw new Exception("Cannot identify the status bar's native monitor.");
        if(Math.Abs(b.Left-data.Monitor.Left)>2 || Math.Abs(b.Right-data.Monitor.Right)>2 ||
            Math.Abs(b.Top-data.Monitor.Top)>2 || b.Bottom<=b.Top || Math.Abs(data.Work.Top-b.Bottom)>2)
            throw new Exception(String.Format("Expected the monitor work area immediately below its full-width upper AppBar (monitor top {0}, bar {1}..{2}, work top {3}).",
                data.Monitor.Top,b.Top,b.Bottom,data.Work.Top));
        Console.WriteLine("PASS Windows reserved {0} physical pixels at the top of the monitor for Dock PID {1}",b.Bottom-b.Top,dock.Id);
    }

    internal static void AttachToStatusBar(int dockId,int guardianId,string scenario,bool allowLegacyStart,bool requireMixedDpi)
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        using(dock=Process.GetProcessById(dockId))
        using(var guardian=Process.GetProcessById(guardianId))
        using(var watch=new ShellVisibilityWatch(true))
        {
            var cursor=Cursor.Position;
            var foreground=GetForegroundWindow();
            var shell=ExplorerTaskbar();
            if(shell==IntPtr.Zero || IsWindowVisible(shell) || VisibleNativeOverflow()!=IntPtr.Zero)
                throw new Exception("The live check starts with Windows taskbar and overflow hidden.");
            if(ShellFlyout(foreground)) throw new Exception("A Windows flyout is already foreground.");
            Console.WriteLine("PASS running Dock PID {0}, matching guardian PID {1}, existing Explorer taskbar hidden",dockId,guardianId);

            var bars=Roots().Where(r=>r.Current.AutomationId=="StatusBar" && Visible(r)).ToArray();
            if(bars.Length==0 && scenario!="WorkArea") throw new Exception("No visible status bar belongs to the selected Dock PID.");
            foreach(var item in bars) {
                CheckAppBar(item);
                if(!allowLegacyStart && Control(item,"StatusBar.Start")!=null)
                    throw new Exception("Unexpected Start button in the upper status bar.");
            }
            if(!allowLegacyStart) Console.WriteLine("PASS upper status bars have no Start button");
            var bar=bars.FirstOrDefault();
            try
            {
                if(scenario=="All" || scenario=="WorkArea") CheckMaximize();
                if(scenario=="All" || scenario=="Start") CheckStartEntries(bar);
                if(scenario=="All" || scenario=="Notifications") CheckLiveNotifications(bar);
                if(scenario=="All" || scenario=="Tray")
                    CheckTrayBars(bars,true,requireMixedDpi,expanded=>CheckExpandedAppBar(expanded,shell));
                if(scenario!="Start" && scenario!="Notifications" && scenario!="Tray" && scenario!="WorkArea")
                    CheckStatusMenus(bar,scenario=="All" ? "Menus" : scenario);
                if(dock.HasExited || guardian.HasExited || ExplorerTaskbar()!=shell)
                    throw new Exception("One of the original processes or the Explorer taskbar was replaced.");
                foreach(var item in bars) CheckAppBar(item);
            }
            finally
            {
                SetCursorPos(cursor.X,cursor.Y);
                if(foreground!=IntPtr.Zero) SetForegroundWindow(foreground);
            }
        }
    }
}
