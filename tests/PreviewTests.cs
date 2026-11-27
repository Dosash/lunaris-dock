using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;
using Lunaris;
using Point = System.Drawing.Point;
using AE = System.Windows.Automation.AutomationElement;

internal static class PreviewTests
{
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int x, int y, uint data, UIntPtr info);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    delegate bool EnumWindow(IntPtr h,IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindow callback,IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent,EnumWindow callback,IntPtr data);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr h,int command);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern int GetApplicationUserModelId(IntPtr process,ref uint length,StringBuilder value);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr handle, out NativeRect r);
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left,Top,Right,Bottom; }
    const uint FixtureMode = 0x8000 + 314;
    static Process dock;
    static string exe;
    static string folderImage;
    static readonly TreeScope Desc = TreeScope.Descendants;

    static AE Find(AE container, AutomationProperty property, object value)
    {
        if (container == null) return null;
        return container.FindFirst(Desc, new PropertyCondition(property, value));
    }
    static AE[] Roots()
    {
        return AE.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AE.ProcessIdProperty, dock.Id)).Cast<AE>().ToArray();
    }
    static AE Window(string id)
    {
        if (id=="Dock") return DockAt(Screen.PrimaryScreen);
        return Roots().FirstOrDefault(w => w.Current.AutomationId == id && !w.Current.IsOffscreen);
    }
    static AE DockAt(Screen screen)
    {
        return Roots().FirstOrDefault(w => {
            if(w.Current.AutomationId!="Dock" || w.Current.IsOffscreen) return false;
            var rect=w.Current.BoundingRectangle;
            return !rect.IsEmpty && screen.Bounds.Contains((int)(rect.Left+rect.Width/2),(int)(rect.Top+rect.Height/2));
        });
    }
    static Point Edge(Screen screen)
    {
        return new Point(screen.Bounds.Left+screen.Bounds.Width/2, screen.Bounds.Bottom-1);
    }
    static AE Reveal(Screen screen)
    {
        var edge=Edge(screen);
        SetCursorPos(edge.X,edge.Y);
        var bar=WaitFor(()=>DockAt(screen),"visible Dock on "+screen.DeviceName);
        Thread.Sleep(250);
        return bar;
    }
    static AE Menu(string name)
    {
        return Roots().Select(r => Find(r, AE.NameProperty, name)).FirstOrDefault(w => w != null && !w.Current.IsOffscreen);
    }
    static void Until(Func<bool> check, string label)
    {
        var sw = Stopwatch.StartNew();
        do {
            try { if (check()) { Console.WriteLine("PASS " + label); return; } } catch (ElementNotAvailableException) { }
            Thread.Sleep(110);
        } while (sw.ElapsedMilliseconds < 18000);
        throw new Exception(label);
    }
    static AE WaitFor(Func<AE> find, string label)
    {
        AE result = null;
        Until(delegate { result = find(); return result != null; }, label);
        return result;
    }
    static void Start()
    {
        dock = Process.Start(new ProcessStartInfo(exe, "--preview") { UseShellExecute = false });
        Reveal(Screen.PrimaryScreen);
        Until(delegate {
            var saved=SettingsStore.Load();
            return !saved.InitializePins && saved.Pins.Count>0 && Buttons(Window("Dock"),"Dock.App").Length>=saved.Pins.Count;
        }, "initial pin discovery and Dock layout settled");
    }
    static void Command(string cmd)
    {
        using (var client = Process.Start(new ProcessStartInfo(exe, cmd) { UseShellExecute = false }))
            if (!client.WaitForExit(18000) || client.ExitCode != 0) throw new Exception("IPC " + cmd);
    }
    static void Stop()
    {
        if (dock == null || dock.HasExited) return;
        Command("--exit");
        if (!dock.WaitForExit(4000)) throw new Exception("Dock did not exit");
    }
    static Point Center(AE el, double fraction)
    {
        var r = el.Current.BoundingRectangle;
        if (r.IsEmpty) throw new Exception("Element has no clickable bounds: " + el.Current.Name);
        return new Point((int)(r.Left + r.Width * fraction), (int)(r.Top + r.Height * .5));
    }
    static int Owner(IntPtr hwnd)
    {
        int pid;
        GetWindowThreadProcessId(hwnd, out pid);
        return pid;
    }
    static void Move(Point p)
    {
        if (Owner(WindowFromPoint(p)) != dock.Id) throw new Exception("Target is covered: " + p);
        SetCursorPos(p.X, p.Y);
    }
    static void Click(AE e, bool right)
    {
        Move(Center(e, .5)); Thread.Sleep(90);
        mouse_event(right ? 8U : 2U, 0, 0, 0, UIntPtr.Zero);
        mouse_event(right ? 16U : 4U, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(230);
    }
    static void Drag(AE source, AE target, bool cancel, IList<string> saved)
    {
        var a = Center(source, .5); var b = Center(target, .2);
        Move(a);
        mouse_event(2, 0, 0, 0, UIntPtr.Zero);
        try {
            for (int i=1; i<=15; ++i)
            {
                SetCursorPos(a.X + (b.X-a.X)*i/15, a.Y + (b.Y-a.Y)*i/15);
                Thread.Sleep(38);
            }
            Until(()=>Math.Abs(Center(target,.2).X-b.X)>10 && Math.Abs(Center(source,.5).X-b.X)<12,
                "drag moves the picked icon and its neighbour before release");
            if(Screen.AllScreens.Length>1 && SettingsStore.Load().Monitor=="all")
            {
                var second=Screen.AllScreens.First(s=>!s.Primary);
                Until(delegate {
                    var buttons=Buttons(DockAt(second),"Dock.App");
                    var moved=buttons.FirstOrDefault(s=>s.Current.Name==source.Current.Name);
                    var neighbour=buttons.FirstOrDefault(s=>s.Current.Name==target.Current.Name);
                    return moved!=null && neighbour!=null && Center(moved,.5).X<Center(neighbour,.5).X;
                },"second monitor mirrors insertion before drop");
            }
            if(!saved.SequenceEqual(SettingsStore.Load().Pins)) throw new Exception("Drag preview wrote settings before drop.");
            if(cancel) SendKeys.SendWait("{ESC}");
        } finally { mouse_event(4, 0, 0, 0, UIntPtr.Zero); }
    }
    static void Keys(string input)
    {
        if (Owner(GetForegroundWindow()) != dock.Id) throw new Exception("Launchpad lacks foreground keyboard focus.");
        SendKeys.SendWait(input);
        Thread.Sleep(180);
    }
    static AE[] Buttons(AE window, string id)
    {
        if (window == null) return new AE[0];
        return window.FindAll(Desc, new PropertyCondition(AE.AutomationIdProperty, id)).Cast<AE>().ToArray();
    }
    static AE OpenLaunchpad(Screen monitor = null)
    {
        monitor = monitor ?? Screen.PrimaryScreen;
        var bar = Reveal(monitor);
        Click(Find(bar, AE.AutomationIdProperty, "OpenLaunchpad"), false);
        var pad = WaitFor(() => Window("Launchpad"), "mouse opens Launchpad");
        Until(() => Tiles(pad).Length > 0, "populated first page");
        var rect=pad.Current.BoundingRectangle;
        var work=monitor.WorkingArea;
        if(rect.Width>=work.Width && rect.Height>=work.Height) throw new Exception("Launchpad unexpectedly covers the whole work area.");
        var start=Find(bar,AE.AutomationIdProperty,"OpenLaunchpad");
        if(rect.Bottom>start.Current.BoundingRectangle.Top+2 ||
            WindowFromPoint(Center(start,.5))!=new IntPtr(bar.Current.NativeWindowHandle))
            throw new Exception("Launchpad covers the Dock icon row.");
        if(Find(pad,AE.AutomationIdProperty,"Launchpad.Title")==null) throw new Exception("Missing Launchpad title.");
        return pad;
    }
    sealed class FixtureForm : Form
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == FixtureMode)
            {
                int mode = m.WParam.ToInt32();
                TopMost = false;
                WindowState = FormWindowState.Normal;
                FormBorderStyle = mode == 2 ? FormBorderStyle.None : FormBorderStyle.Sizable;
                if(mode==0) Bounds=new Rectangle(Screen.PrimaryScreen.WorkingArea.Left+150,Screen.PrimaryScreen.WorkingArea.Top+110,350,200);
                if (mode == 1) WindowState = FormWindowState.Maximized;
                if (mode == 2) { Bounds = Screen.PrimaryScreen.Bounds; TopMost = true; }
                if (mode == 4)
                {
                    var other=Screen.AllScreens.First(s=>!s.Primary).WorkingArea;
                    Bounds=new Rectangle(other.Left+50,other.Top+50,350,200);
                }
                if (mode == 3) WindowState = FormWindowState.Minimized;
                else Activate();
                return;
            }
            base.WndProc(ref m);
        }
    }
    static void Fixtures(string tag)
    {
        var work=Screen.PrimaryScreen.WorkingArea;
        using (var first = new FixtureForm { Text=tag+" A", StartPosition=FormStartPosition.Manual, Bounds=new Rectangle(work.Left+90,work.Top+75,350,200) })
        using (var second = new FixtureForm { Text=tag+" B", StartPosition=FormStartPosition.Manual, Bounds=new Rectangle(work.Left+150,work.Top+110,350,200) })
        {
            second.Show(); Application.Run(first);
        }
    }
    static void CheckWindows()
    {
        string tag = "Lunaris-" + Guid.NewGuid().ToString("N");
        string host = Process.GetCurrentProcess().MainModule.FileName;
        using (var fixture = Process.Start(new ProcessStartInfo(host,"--fixtures " +tag) { UseShellExecute=false, CreateNoWindow=true }))
        using (var platform = new PlatformServices(true))
        {
            IntPtr a = IntPtr.Zero, b = IntPtr.Zero;
            try
            {
                Until(delegate { a=FindWindow(null,tag+" A"); b=FindWindow(null,tag+" B");
                    return a!=IntPtr.Zero && b!=IntPtr.Zero && Owner(a)==fixture.Id && Owner(b)==fixture.Id; }, "two real top-level windows of one program");
                var app = platform.GetWindows().First(w => w.Handle==a).App;
                Func<AE> button = () => Buttons(Window("Dock"),"Dock.App").FirstOrDefault(s => s.Current.Name == app.Name);
                WaitFor(button, "fixture grouped in Dock");
                PostMessage(a,FixtureMode,new IntPtr(3),IntPtr.Zero);
                Until(()=>IsIconic(a), "first fixture window minimized");
                foreach(var hwnd in new[]{a,b})
                {
                    Click(button(),false);
                    string title = tag + (hwnd==a ? " A" : " B");
                    Click(WaitFor(()=>Menu(title), "group menu contains "+title),false);
                    Until(()=>GetForegroundWindow()==hwnd && !IsIconic(hwnd), "Dock activates exact HWND for " + title);
                }
                var other=Screen.AllScreens.FirstOrDefault(s=>!s.Primary);
                if(other!=null)
                {
                    PostMessage(a,FixtureMode,new IntPtr(4),IntPtr.Zero);
                    Until(()=>Screen.FromHandle(a).DeviceName==other.DeviceName,"ordinary fixture window moved to second display");
                }
                PostMessage(b,FixtureMode,new IntPtr(1),IntPtr.Zero);
                Until(()=>IsZoomed(b) && GetForegroundWindow()==b, "maximized ordinary window");
                var monitor=Screen.FromHandle(b);
                var away=new Point(monitor.WorkingArea.Left+monitor.WorkingArea.Width/2,monitor.WorkingArea.Top+monitor.WorkingArea.Height/2);
                SetCursorPos(away.X,away.Y);
                Until(()=>DockAt(monitor)==null,"maximized window hides its Dock");
                if(other!=null)
                    Until(()=>DockAt(other)!=null && DockAt(monitor)==null,"ordinary second display retains its Dock while primary is covered");
                var bar=Reveal(monitor);
                var icon=Find(bar,AE.AutomationIdProperty,"OpenLaunchpad");
                var previous=Center(icon,.5);
                Move(previous); Thread.Sleep(450);
                Until(()=>DockAt(monitor)!=null && Owner(WindowFromPoint(previous))==dock.Id,"edge reveals clickable Dock above maximized window");
                SetCursorPos(away.X,away.Y);
                Until(()=>DockAt(monitor)==null && Owner(WindowFromPoint(previous))!=dock.Id,"pointer departure hides Dock and releases the former hit area");
                var prefs=OpenSettings(monitor);
                SetCursorPos(away.X,away.Y);
                StableDock(monitor,"open settings hold the Dock above a maximized window");
                Toggle(prefs,"HideDockWhenCovered",false);
                Until(()=>Window("Settings")!=null && !SettingsStore.Load().HideDockWhenCovered,
                    "auto-hide change saved while preferences remain open");
                CloseSettings(prefs);
                PostMessage(b,FixtureMode,new IntPtr(1),IntPtr.Zero);
                Until(()=>IsZoomed(b) && GetForegroundWindow()==b,"maximized fixture active with auto-hide disabled");
                SetCursorPos(away.X,away.Y);
                StableDock(monitor,"disabled auto-hide keeps Dock visible over the active maximized window");
                PostMessage(b,FixtureMode,new IntPtr(2),IntPtr.Zero);
                Until(()=>!IsZoomed(b) && GetForegroundWindow()==b && Screen.FromHandle(b).Bounds==FixtureBounds(b),
                    "topmost borderless fixture covers the display and has foreground");
                StableDock(monitor,"disabled auto-hide keeps the Dock visible when the topmost fullscreen fixture appears");
                Until(delegate {
                    var topDock=DockAt(monitor);
                    var visible=Find(topDock,AE.AutomationIdProperty,"OpenLaunchpad");
                    return visible!=null && WindowFromPoint(Center(visible,.5))==new IntPtr(topDock.Current.NativeWindowHandle) &&
                        GetForegroundWindow()==b && Cursor.Position==away;
                },"Dock remains clickable above the new topmost fullscreen window without stealing focus or approaching the edge");
                PostMessage(b,FixtureMode,new IntPtr(1),IntPtr.Zero);
                Until(()=>IsZoomed(b) && GetForegroundWindow()==b,"fixture returned to maximized mode with Dock still enabled");
                prefs=OpenSettings(monitor);
                Toggle(prefs,"HideDockWhenCovered",true);
                Until(()=>SettingsStore.Load().HideDockWhenCovered,"auto-hide re-enabled immediately");
                SetCursorPos(away.X,away.Y);
                StableDock(monitor,"preferences keep Dock visible after re-enabling auto-hide");
                CloseSettings(prefs);
                PostMessage(b,FixtureMode,new IntPtr(1),IntPtr.Zero);
                SetCursorPos(away.X,away.Y);
                Until(()=>IsZoomed(b) && GetForegroundWindow()==b && DockAt(monitor)==null,
                    "Dock hides again after closing preferences");
                PostMessage(b,FixtureMode,new IntPtr(2),IntPtr.Zero);
                Until(()=>GetForegroundWindow()==b && DockAt(monitor)==null,"Dock yields to borderless fullscreen");
                bar=Reveal(monitor);
                var fullIcon=Find(bar,AE.AutomationIdProperty,"OpenLaunchpad");
                Until(()=>Owner(WindowFromPoint(Center(fullIcon,.5)))==dock.Id,"edge reveals Dock above fullscreen on the same screen");
                SetCursorPos(away.X,away.Y);
                Until(()=>DockAt(monitor)==null,"fullscreen Dock hides again after pointer departure");
                PostMessage(b,FixtureMode,IntPtr.Zero,IntPtr.Zero);
                Until(()=>DockAt(monitor)!=null,"Dock returns after fullscreen");
            }
            finally
            {
                if (a!=IntPtr.Zero) PostMessage(a,0x10,IntPtr.Zero,IntPtr.Zero);
                if (!fixture.WaitForExit(3000)) { fixture.Kill(); fixture.WaitForExit(); }
            }
        }
    }
    static Bitmap CaptureIcon(AE button)
    {
        var r = button.Current.BoundingRectangle;
        if (r.IsEmpty || r.Width<3 || r.Height<3) throw new Exception("Cannot capture a clipped or hidden icon: "+button.Current.Name+" "+r);
        var image = new Bitmap((int)r.Width, (int)r.Height);
        using (var graphics = Graphics.FromImage(image))
            graphics.CopyFromScreen((int)r.Left,(int)r.Top,0,0,image.Size);
        return image;
    }
    static double GlyphSpan(Bitmap image)
    {
        // Measure the distance between colour centroids in the actual Start artwork.
        // Background, tooltip shadow, focus and native cursor pixels do not contribute.
        var palette = new[]{Color.FromArgb(112,204,254),Color.FromArgb(140,151,255),
                            Color.FromArgb(101,212,198),Color.FromArgb(192,145,240)};
        var count=new int[4]; var xsum=new double[4]; var ysum=new double[4];
        for(int y=0;y<image.Height;y++) for(int x=0;x<image.Width;x++)
        {
            Color pixel=image.GetPixel(x,y);
            for(int i=0;i<palette.Length;i++)
            {
                Color tile=palette[i];
                if(Math.Abs(pixel.R-tile.R)+Math.Abs(pixel.G-tile.G)+Math.Abs(pixel.B-tile.B)>25) continue;
                count[i]++; xsum[i]+=x; ysum[i]+=y; break;
            }
        }
        if(count.Any(n=>n<5)) throw new Exception("Four Start icon colours were not visible: "+String.Join(",",count));
        for(int i=0;i<4;i++) { xsum[i]/=count[i]; ysum[i]/=count[i]; }
        return ((xsum[1]-xsum[0])+(xsum[3]-xsum[2])+(ysum[2]-ysum[0])+(ysum[3]-ysum[1]))/4;
    }
    static void CheckHover(bool magnify)
    {
        var monitor=Screen.PrimaryScreen;
        Reveal(monitor);
        var edge=Edge(monitor);
        // Keep the Dock open from its activation edge without entering a button.
        // Waiting on a neighbour instead would prime WPF's between-tooltip timer.
        SetCursorPos(edge.X,edge.Y);
        var stable=Stopwatch.StartNew();
        System.Windows.Rect last=System.Windows.Rect.Empty;
        AE button=null;
        Until(delegate {
            var current=Find(DockAt(monitor),AE.AutomationIdProperty,"OpenLaunchpad");
            if(current==null) { stable.Restart(); return false; }
            var r=current.Current.BoundingRectangle;
            if(r.IsEmpty || r.Width<3 || r.Height<3 || !monitor.Bounds.Contains(Center(current,.5)) ||
                Owner(WindowFromPoint(Center(current,.5)))!=dock.Id) { stable.Restart(); return false; }
            if(r!=last || r.Contains(Cursor.Position.X,Cursor.Position.Y))
            {
                SetCursorPos(edge.X,edge.Y);
                last=r; stable.Restart();
            }
            button=current; return stable.ElapsedMilliseconds>=1350;
        }, "primary Dock icon has stable visible pixel bounds after window changes");
        using (var beforeHover = CaptureIcon(button))
        {
            Move(new Point((int)last.Left+3,(int)last.Top+4));
            Thread.Sleep(240);
            var fresh=Find(DockAt(monitor),AE.AutomationIdProperty,"OpenLaunchpad");
            if(fresh==null || fresh.Current.BoundingRectangle!=last) throw new Exception("Dock geometry changed during hover comparison");
            using (var afterHover = CaptureIcon(fresh))
            {
                double before=GlyphSpan(beforeHover),after=GlyphSpan(afterHover);
                string measured=String.Format("Start tile spacing {0:F2} -> {1:F2} px (magnification {2})",before,after,magnify);
                if(magnify ? after < before*1.1 || after > before*1.32 : Math.Abs(after-before)>.75)
                    throw new Exception(measured);
                Console.WriteLine("PASS "+measured);
            }
        }
    }
    static int DockHeight()
    {
        var bar=Window("Dock");
        NativeRect r;
        if (bar==null || !GetWindowRect(new IntPtr(bar.Current.NativeWindowHandle),out r)) return 0;
        return r.Bottom-r.Top;
    }
    static Rectangle FixtureBounds(IntPtr hwnd)
    {
        NativeRect r;
        return GetWindowRect(hwnd,out r) ? Rectangle.FromLTRB(r.Left,r.Top,r.Right,r.Bottom) : Rectangle.Empty;
    }
    static AE OpenSettings(Screen screen)
    {
        var bar=Reveal(screen);
        Click(Find(bar,AE.AutomationIdProperty,"OpenLaunchpad"),true);
        Click(WaitFor(()=>Menu("Настройки…"),"Dock context menu exposes settings"),false);
        return WaitFor(()=>Window("Settings"),"settings window opened");
    }
    static void StableDock(Screen monitor,string label)
    {
        var watch=Stopwatch.StartNew();
        do {
            if(DockAt(monitor)==null) throw new Exception(label);
            Thread.Sleep(110);
        } while(watch.ElapsedMilliseconds<1300);
        Console.WriteLine("PASS "+label);
    }
    static void CloseSettings(AE settings)
    {
        Click(Find(settings,AE.AutomationIdProperty,"CloseSettings"),false);
        Until(()=>Window("Settings")==null,"settings window closed");
    }
    static void Toggle(AE settings,string id,bool value)
    {
        var element=WaitFor(()=>Find(settings,AE.AutomationIdProperty,id),"preference "+id+" is available");
        var toggle=(TogglePattern)element.GetCurrentPattern(TogglePattern.Pattern);
        if((toggle.Current.ToggleState==ToggleState.On)!=value) toggle.Toggle();
    }
    static void SelectMonitor(Screen from,string choice)
    {
        var settings=OpenSettings(from);
        var combo=Find(settings,AE.AutomationIdProperty,"DockMonitor");
        var list=(ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        list.Expand();
        var option=WaitFor(()=>Roots().SelectMany(r=>r.FindAll(Desc,new PropertyCondition(AE.AutomationIdProperty,choice)).Cast<AE>())
            .FirstOrDefault(e=>!e.Current.IsOffscreen && e.Current.BoundingRectangle.Width>2 &&
                   e.Current.BoundingRectangle.Height>2 && Owner(WindowFromPoint(Center(e,.5)))==dock.Id),
            "selectable monitor mode " + choice);
        Click(option,false);
        if(list.Current.ExpandCollapseState==ExpandCollapseState.Expanded) list.Collapse();
        Until(delegate {
            string selected=SettingsStore.Load().Monitor;
            return Window("Settings")!=null && (choice=="Monitor.all" ? selected=="all" : choice=="Monitor.primary" ? selected=="primary" :
                !String.IsNullOrEmpty(selected) && selected!="all" && selected!="primary");
        }, "monitor mode persists immediately while settings are open: "+choice);
        CloseSettings(settings);
    }
    static void VerifyScreens(string firstPinned)
    {
        var other=Screen.AllScreens.Where(s=>!s.Primary).OrderBy(s=>s.DeviceName,StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if(other==null) { Console.WriteLine("SKIP multi-monitor scenario: one connected display"); return; }
        var secondary=Reveal(other);
        Until(()=>Buttons(secondary,"Dock.App")[0].Current.Name==firstPinned,"secondary Dock reflects committed pin order after restart");
        var button=Find(secondary,AE.AutomationIdProperty,"OpenLaunchpad");
        Click(button,false);
        var pad=WaitFor(()=>Window("Launchpad"),"secondary Dock opens its Launchpad");
        var p=Center(pad,.5);
        if(!other.Bounds.Contains(p)) throw new Exception("Launchpad opened on the wrong monitor.");
        Click(button,false);
        Until(()=>Window("Launchpad")==null,"second click on same Dock closes its floating Launchpad");
        SelectMonitor(Screen.PrimaryScreen,"Monitor.1");
        Reveal(other);
        var edge=Edge(Screen.PrimaryScreen); SetCursorPos(edge.X,edge.Y); Thread.Sleep(450);
        Until(()=>DockAt(Screen.PrimaryScreen)==null,"single secondary monitor selection removes primary Dock");
        SelectMonitor(other,"Monitor.primary");
        Reveal(Screen.PrimaryScreen);
        edge=Edge(other); SetCursorPos(edge.X,edge.Y); Thread.Sleep(450);
        Until(()=>DockAt(other)==null,"primary-only selection removes secondary Dock");
        SetCursorPos(other.Bounds.Left+other.Bounds.Width/2,other.Bounds.Top+other.Bounds.Height/2);
        Command("--toggle");
        var fallback=WaitFor(()=>Window("Launchpad"),"keyboard IPC opened Launchpad with pointer on unselected screen");
        if(!Screen.PrimaryScreen.Bounds.Contains(Center(fallback,.5))) throw new Exception("IPC missed the selected Dock monitor.");
        Keys("{ESC}");
        Until(()=>Window("Launchpad")==null,"fallback Launchpad dismissed");
        SelectMonitor(Screen.PrimaryScreen,"Monitor.all");
        Reveal(Screen.PrimaryScreen);
    }
    static Color PlateColor()
    {
        var button=Find(Reveal(Screen.PrimaryScreen),AE.AutomationIdProperty,"OpenLaunchpad");
        using(var picture=CaptureIcon(button))
        {
            int r=0,g=0,b=0;
            for(int y=1;y<5;y++) for(int x=1;x<5;x++) {
                var c=picture.GetPixel(x,y); r+=c.R;g+=c.G;b+=c.B;
            }
            return Color.FromArgb(r/16,g/16,b/16);
        }
    }
    static double PlateLightness()
    {
        var c=PlateColor();
        return (c.R+c.G+c.B)/3.0;
    }
    static void ChooseTheme(AE settings,string theme)
    {
        var option=Find(settings,AE.AutomationIdProperty,"Theme."+theme);
        Click(option,false);
    }
    static void CheckSettings()
    {
        CheckHover(true);
        int initialHeight = DockHeight();
        var settings=OpenSettings(Screen.PrimaryScreen);
        var size = Find(settings, AE.AutomationIdProperty, "DockSize");
        ((RangeValuePattern)size.GetCurrentPattern(RangeValuePattern.Pattern)).SetValue(72);
        Toggle(settings,"Magnify",false);
        ChooseTheme(settings,"Dark");
        Toggle(settings,"HideWindowsStart",true);
        Until(delegate {
            var saved=SettingsStore.Load();
            return Window("Settings")!=null && saved.Size==72 && !saved.Magnify && saved.HideWindowsStart &&
                   saved.Theme=="dark" && DockHeight()>initialHeight;
        },"size, magnification, theme and native Start preference persist while settings remain open; live Dock resized");
        CloseSettings(settings);
        CheckHover(false);
        double dark=PlateLightness();
        settings=OpenSettings(Screen.PrimaryScreen);
        ChooseTheme(settings,"Light");
        Until(()=>Window("Settings")!=null && SettingsStore.Load().Theme=="light","light theme changed immediately");
        double light=PlateLightness();
        if(light-dark<70) throw new Exception("Light and dark Dock surfaces failed pixel comparison: "+light+" / "+dark);
        Console.WriteLine("PASS live light/dark Dock surfaces: "+light.ToString("F1")+" / "+dark.ToString("F1"));
        ChooseTheme(settings,"Custom");
        var ids=new[]{"CustomSurface","CustomText","CustomAccent"};
        var values=new[]{"#38345D","#FFF4DE","#B4DCC6"};
        for(int i=0;i<ids.Length;i++) {
            var e=WaitFor(()=>Find(settings,AE.AutomationIdProperty,ids[i]),"editable custom theme colour "+ids[i]);
            ((ValuePattern)e.GetCurrentPattern(ValuePattern.Pattern)).SetValue(values[i]);
        }
        Until(delegate {
            var s=SettingsStore.Load();
            return s.Theme=="custom" && s.CustomSurface==values[0] && s.CustomText==values[1] && s.CustomAccent==values[2];
        },"all three custom HEX colours persisted immediately");
        Until(delegate { var c=PlateColor();return c.B>c.R+15 && c.B>c.G+20; },"custom purple surface visible on the actual Dock");
        var surface=Find(settings,AE.AutomationIdProperty,ids[0]);
        var editor=(ValuePattern)surface.GetCurrentPattern(ValuePattern.Pattern);
        surface.SetFocus(); editor.SetValue("#XYZ");
        Find(settings,AE.AutomationIdProperty,ids[1]).SetFocus();
        Until(()=>editor.Current.Value==values[0] && SettingsStore.Load().CustomSurface==values[0],
            "invalid HEX recovers the previous valid surface after focus leaves the editor");
        var close=Center(Find(settings,AE.AutomationIdProperty,"CloseSettings"),.5);
        var bounds=settings.Current.BoundingRectangle;
        if(close.X<bounds.Left+bounds.Width*.8 || close.Y>bounds.Top+bounds.Height*.2)
            throw new Exception("Settings close button is not in the upper-right corner.");
        CloseSettings(settings);
        Stop(); Start();
        settings=OpenSettings(Screen.PrimaryScreen);
        var native=(TogglePattern)Find(settings,AE.AutomationIdProperty,"HideWindowsStart").GetCurrentPattern(TogglePattern.Pattern);
        var restoredSize=(RangeValuePattern)Find(settings,AE.AutomationIdProperty,"DockSize").GetCurrentPattern(RangeValuePattern.Pattern);
        Until(()=>native.Current.ToggleState==ToggleState.On && restoredSize.Current.Value==72,
              "restarted preferences restore native Start option and Dock size");
        var selected=(SelectionItemPattern)Find(settings,AE.AutomationIdProperty,"Theme.Custom").GetCurrentPattern(SelectionItemPattern.Pattern);
        Until(()=>selected.Current.IsSelected && ids.Select(id=>((ValuePattern)Find(settings,AE.AutomationIdProperty,id)
                .GetCurrentPattern(ValuePattern.Pattern)).Current.Value).SequenceEqual(values),
              "restarted preferences restore all three custom colours and the selected theme");
        ChooseTheme(settings,"System");
        Until(()=>Window("Settings")!=null && SettingsStore.Load().Theme=="system","system theme changed immediately");
        Toggle(settings,"HideWindowsStart",false);
        Until(()=>!SettingsStore.Load().HideWindowsStart,"native Start preference re-enabled in preview profile");
        CloseSettings(settings);
    }
    static AE[] Tiles(AE pad)
    {
        return pad.FindAll(Desc,new OrCondition(new PropertyCondition(AE.AutomationIdProperty,"Launchpad.App"),
            new PropertyCondition(AE.AutomationIdProperty,"Launchpad.Folder"))).Cast<AE>().ToArray();
    }
    static AE Action(string id)
    {
        return WaitFor(()=>Roots().Select(r=>Find(r,AE.AutomationIdProperty,id))
            .FirstOrDefault(item=>item!=null && !item.Current.IsOffscreen),"available command "+id);
    }
    static void RootCatalog(AE pad)
    {
        Click(Find(pad,AE.AutomationIdProperty,"Launchpad.Category.All"),false);
        Keys("^{HOME}");
    }
    static AE OnPages(AE pad,Func<AE,bool> match)
    {
        var seen=new HashSet<string>();
        while(true)
        {
            var tiles=Tiles(pad);
            var tile=tiles.FirstOrDefault(match);
            if(tile!=null) return tile;
            string signature=String.Join("\n",tiles.Select(t=>t.Current.AutomationId+"="+t.Current.Name));
            var next=Find(pad,AE.AutomationIdProperty,"Launchpad.Next");
            if(next==null || !next.Current.IsEnabled || !seen.Add(signature)) return null;
            Click(next,false);
        }
    }
    static AE LibraryFolder(AE pad,string name)
    {
        RootCatalog(pad);
        var item=OnPages(pad,t=>t.Current.AutomationId=="Launchpad.Folder" && t.Current.Name==name);
        if(item==null) throw new Exception("Missing root folder "+name);
        return item;
    }
    static void FillFolderName(AE pad,string name)
    {
        var field=WaitFor(()=>Find(pad,AE.AutomationIdProperty,"Launchpad.Folder.Name"),"editable folder name");
        ((ValuePattern)field.GetCurrentPattern(ValuePattern.Pattern)).SetValue(name);
        Click(Find(pad,AE.AutomationIdProperty,"Launchpad.Folder.Save"),false);
    }
    static void MoveApp(AE pad,string appName,string folderName)
    {
        RootCatalog(pad);
        var app=OnPages(pad,t=>t.Current.AutomationId=="Launchpad.App" && t.Current.Name==appName);
        if(app==null) throw new Exception("Missing root application "+appName);
        Click(app,true);
        Click(Action("Launchpad.Item.Move"),false);
        var query=Find(pad,AE.AutomationIdProperty,"Launchpad.Picker.Search");
        ((ValuePattern)query.GetCurrentPattern(ValuePattern.Pattern)).SetValue(folderName);
        var target=WaitFor(()=>Buttons(pad,"Launchpad.Move.Target").FirstOrDefault(t=>t.Current.Name==folderName),
              "folder available as a move destination");
        ((InvokePattern)target.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        Until(()=>Find(pad,AE.AutomationIdProperty,"Launchpad.Move.Target")==null,"move picker closed");
        Click(LibraryFolder(pad,folderName),false);
        WaitFor(()=>Buttons(pad,"Launchpad.App").FirstOrDefault(t=>t.Current.Name==appName),
            "moved app appears in the chosen folder: "+appName);
    }
    static void DragIntoNestedFolder(AE pad,string[] names)
    {
        const string nested="Вложенная папка";
        Click(Find(pad,AE.AutomationIdProperty,"Launchpad.More"),false);
        Click(Action("Launchpad.Action.CreateFolder"),false);
        FillFolderName(pad,nested);
        Func<AE> target=()=>Buttons(pad,"Launchpad.Folder").FirstOrDefault(t=>t.Current.Name==nested);
        var folder=WaitFor(target,"nested folder is on the same page as both apps");
        var app=Buttons(pad,"Launchpad.App").First(t=>t.Current.Name==names[0]);
        var start=Center(app,.5);
        var end=Center(folder,.5);
        Move(start);
        mouse_event(2,0,0,0,UIntPtr.Zero);
        try {
            for(int i=1;i<=20;i++) {
                SetCursorPos(start.X+(end.X-start.X)*i/20,start.Y+(end.Y-start.Y)*i/20);
                Thread.Sleep(35);
            }
            Thread.Sleep(150);
        } finally { mouse_event(4,0,0,0,UIntPtr.Zero); }
        Until(()=>Buttons(pad,"Launchpad.App").Select(t=>t.Current.Name).SequenceEqual(names.Skip(1)),
            "physical drag to the center of a folder removes only the source tile from its parent");
        Click(target(),false);
        Until(()=>Buttons(pad,"Launchpad.App").Select(t=>t.Current.Name).SequenceEqual(names.Take(1)),
            "nested folder contains the actual dragged app");
        Click(Find(pad,AE.AutomationIdProperty,"Launchpad.Folder.Back"),false);
        Click(WaitFor(target,"back returns to the parent of the drop destination"),true);
        Click(Action("Launchpad.Folder.Remove"),false);
        Until(()=>Buttons(pad,"Launchpad.App").Select(t=>t.Current.Name).SequenceEqual(names),
            "removing the nested folder restores its app to the alphabetized parent");
    }
    static void CheckLibrary()
    {
        var pad=OpenLaunchpad();
        RootCatalog(pad);
        var dirs=new[]{Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                       Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)};
        string[] preferredNames=new string[0];
        using(var platform=new PlatformServices(true))
        {
            var library=new LaunchLibrary(SettingsStore.Load(),platform.GetApplications());
            var rootApps=new HashSet<string>(library.Children(LaunchLibrary.Root).Where(i=>!i.IsFolder).Select(i=>i.Name));
            preferredNames=new[]{"Калькулятор","Calculator","Paint","Блокнот","Notepad","Фотографии","Photos"}
                .Where(rootApps.Contains).Take(2).ToArray();
            var candidates=library.Children(LaunchLibrary.Root).Where(item=>item.IsSystem && item.Id.StartsWith("start:") &&
                dirs.Any(dir=>Directory.Exists(Path.Combine(dir,item.Id.Substring(6))))).ToList();
            if(candidates.Count==0)
            {
                if(dirs.Where(Directory.Exists).Any(d=>Directory.GetDirectories(d).Length>0))
                    throw new Exception("No Start menu folder corresponds to a real Programs subfolder.");
                Console.WriteLine("SKIP system folders: both Programs roots have no subfolders");
            }
            else
            {
                var sourceFolder=candidates.FirstOrDefault(f=>library.Children(f.Id).Any(i=>!i.IsFolder)) ?? candidates[0];
                var systemFolder=OnPages(pad,t=>t.Current.AutomationId=="Launchpad.Folder" && t.Current.Name==sourceFolder.Name &&
                    t.Current.ItemStatus.Contains("Папка Windows"));
                if(systemFolder==null) throw new Exception("Missing real Programs folder "+sourceFolder.Name);
                Click(systemFolder,false);
                var back=WaitFor(()=>Find(pad,AE.AutomationIdProperty,"Launchpad.Folder.Back"),"entered real Programs folder "+sourceFolder.Name);
                var child=library.Children(sourceFolder.Id).FirstOrDefault(i=>!i.IsFolder);
                if(child!=null && OnPages(pad,t=>t.Current.AutomationId=="Launchpad.App" && t.Current.Name==child.Name)==null)
                    throw new Exception("Missing registered application inside Programs folder: "+child.Name);
                if(child!=null) Console.WriteLine("PASS real Programs folder "+sourceFolder.Name+" contains "+child.Name);
                Click(back,false);
                Until(()=>Find(pad,AE.AutomationIdProperty,"Launchpad.Folder.Back")==null ||
                          Find(pad,AE.AutomationIdProperty,"Launchpad.Folder.Back").Current.IsOffscreen,
                          "navigated back from Windows Start folder");
            }
        }
        RootCatalog(pad);
        var first=OnPages(pad,t=>t.Current.AutomationId=="Launchpad.App" &&
            (preferredNames.Length==0 || t.Current.Name==preferredNames[0]));
        if(first==null) throw new Exception("No application in Start root.");
        string nameA=first.Current.Name;
        RootCatalog(pad);
        var second=OnPages(pad,t=>t.Current.AutomationId=="Launchpad.App" && t.Current.Name!=nameA &&
            (preferredNames.Length<2 || t.Current.Name==preferredNames[1]));
        if(second==null) throw new Exception("Need two root applications for move and sort.");
        string nameB=second.Current.Name;
        const string folderName="Lunaris QA Folder";
        const string renamed="Рабочие приложения";
        Click(Find(pad,AE.AutomationIdProperty,"Launchpad.More"),false);
        Click(Action("Launchpad.Action.CreateFolder"),false);
        FillFolderName(pad,folderName);
        var folder=LibraryFolder(pad,folderName);
        if(!folder.Current.ItemStatus.Contains("Личная папка")) throw new Exception("New folder is not a personal folder.");
        MoveApp(pad,nameA,folderName);
        Click(Find(pad,AE.AutomationIdProperty,"Launchpad.More"),false);
        Click(Action("Launchpad.Folder.AddApps"),false);
        var picker=Find(pad,AE.AutomationIdProperty,"Launchpad.Picker.Search");
        ((ValuePattern)picker.GetCurrentPattern(ValuePattern.Pattern)).SetValue(nameB);
        var checkedApp=WaitFor(()=>Buttons(pad,"Launchpad.Picker.App").FirstOrDefault(t=>t.Current.Name==nameB),
              "second application offered by the folder's Add apps picker");
        ((TogglePattern)checkedApp.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
        Click(Find(pad,AE.AutomationIdProperty,"Launchpad.Picker.Apply"),false);
        Until(()=>Buttons(pad,"Launchpad.App").Length==2,"Add apps picker adds the second application to the open folder");
        var items=Buttons(pad,"Launchpad.App");
        if(items.Length!=2) throw new Exception("Expected both applications inside the new folder.");
        string[] reverse={items[1].Current.Name,items[0].Current.Name};
        Click(items[1],true);
        Click(Action("Launchpad.Item.Earlier"),false);
        Until(()=>Buttons(pad,"Launchpad.App").Select(t=>t.Current.Name).SequenceEqual(reverse),
              "manual move changes both visible app positions");
        Click(LibraryFolder(pad,folderName),true);
        Click(Action("Launchpad.Folder.Rename"),false);
        FillFolderName(pad,renamed);
        Command("--toggle");
        Until(()=>Window("Launchpad")==null,"closed organized Launchpad");
        Stop(); Start();
        pad=OpenLaunchpad();
        Click(LibraryFolder(pad,renamed),false);
        Until(()=>Buttons(pad,"Launchpad.App").Select(t=>t.Current.Name).SequenceEqual(reverse),
              "renamed folder, moved apps and manual order survive process restart");
        if(folderImage!=null)
        {
            var bounds=pad.Current.BoundingRectangle;
            var area=Rectangle.FromLTRB((int)Math.Floor(bounds.Left),(int)Math.Floor(bounds.Top),
                                        (int)Math.Ceiling(bounds.Right),(int)Math.Ceiling(bounds.Bottom));
            area.Intersect(Screen.FromPoint(Center(pad,.5)).Bounds);
            SetCursorPos(area.Left+12,area.Top+12);
            Thread.Sleep(300);
            using(var image=new Bitmap(area.Width,area.Height))
            using(var graphics=Graphics.FromImage(image))
            {
                graphics.CopyFromScreen(area.Location,Point.Empty,area.Size,CopyPixelOperation.SourceCopy);
                image.Save(folderImage,System.Drawing.Imaging.ImageFormat.Png);
            }
            Console.WriteLine("PASS captured the reopened two-application folder: "+folderImage);
        }
        Click(Find(pad,AE.AutomationIdProperty,"Launchpad.More"),false);
        Click(Action("Launchpad.Sort.Alphabetical"),false);
        var sorted=reverse.OrderBy(n=>n,StringComparer.CurrentCultureIgnoreCase).ToArray();
        Until(()=>Buttons(pad,"Launchpad.App").Select(t=>t.Current.Name).SequenceEqual(sorted),
              "alphabetical sort applied to folder contents");
        DragIntoNestedFolder(pad,sorted);
        Click(LibraryFolder(pad,renamed),true);
        Click(Action("Launchpad.Folder.Remove"),false);
        foreach(var name in reverse)
        {
            RootCatalog(pad);
            if(OnPages(pad,t=>t.Current.AutomationId=="Launchpad.App" && t.Current.Name==name)==null)
                throw new Exception("Deleted folder did not return app to parent: "+name);
        }
        RootCatalog(pad);
        if(OnPages(pad,t=>t.Current.AutomationId=="Launchpad.Folder" && t.Current.Name==renamed)!=null)
            throw new Exception("Deleted folder still visible.");
        Console.WriteLine("PASS removing a personal folder returns both applications to the Start root");
        Command("--toggle");
        Until(()=>Window("Launchpad")==null,"closed library test");
    }
    static bool HasPackageId(IntPtr hwnd,string expected)
    {
        var processes=new HashSet<int>{Owner(hwnd)};
        EnumChildWindows(hwnd,delegate(IntPtr h,IntPtr d) { processes.Add(Owner(h)); return true; },IntPtr.Zero);
        foreach(int pid in processes)
        {
            IntPtr hp=OpenProcess(0x1000,false,pid);
            if(hp==IntPtr.Zero) continue;
            try {
                uint length=0;
                if(GetApplicationUserModelId(hp,ref length,null)!=122 || length>32768) continue;
                var id=new StringBuilder((int)length);
                if(GetApplicationUserModelId(hp,ref length,id)==0 && String.Equals(id.ToString(),expected,StringComparison.OrdinalIgnoreCase)) return true;
            }
            finally { CloseHandle(hp); }
        }
        return false;
    }
    static bool HasIndicator(AE button)
    {
        if(button==null) return false;
        using(var pixels=CaptureIcon(button))
        {
            int x=pixels.Width/2,contrastRows=0;
            for(int y=pixels.Height-1;y>=Math.Max(0,pixels.Height-5);y--) {
                var dot=pixels.GetPixel(x,y);
                var left=pixels.GetPixel(x-6,y); var right=pixels.GetPixel(x+6,y);
                if(RgbDistance(dot,left)>115 && RgbDistance(dot,right)>115 && RgbDistance(left,right)<30) contrastRows++;
            }
            return contrastRows>=2;
        }
    }
    static int RgbDistance(Color a,Color b) { return Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B); }
    static void StoreCalculator(Screen monitor = null)
    {
        const string id="Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";
        monitor = monitor ?? Screen.PrimaryScreen;
        using(var platform=new PlatformServices(true))
        {
            var app=platform.GetApplications().Single(a=>String.Equals(a.Id,id,StringComparison.OrdinalIgnoreCase));
            var existed=new HashSet<IntPtr>();
            EnumWindows(delegate(IntPtr h,IntPtr d){ existed.Add(h); return true; },IntPtr.Zero);
            IntPtr target=IntPtr.Zero;
            int capturedOwner = 0;
            try
            {
                var pad=OpenLaunchpad();
                var search=Find(pad,AE.AutomationIdProperty,"Search");
                ((ValuePattern)search.GetCurrentPattern(ValuePattern.Pattern)).SetValue(app.Name);
                var tile=WaitFor(()=>Buttons(pad,"Launchpad.App").FirstOrDefault(t=>t.Current.Name==app.Name),"Store Calculator listed in Launchpad");
                Click(tile,false);
                Until(delegate {
                    var foreground=GetForegroundWindow();
                    if(foreground==IntPtr.Zero || !HasPackageId(foreground,id)) return false;
                    target=foreground; capturedOwner=Owner(foreground); return true;
                },"selected Store tile activates real Calculator HWND and OS package AUMID");
                Console.WriteLine("Store AUMID: " + id + ", HWND: " + target + ", already existed: " + existed.Contains(target));
                Until(()=>platform.GetWindows().Any(w=>w.Handle==target && String.Equals(w.AppId,id,StringComparison.OrdinalIgnoreCase)),
                    "Dock window catalog groups Calculator HWND under the Store entry");
                Reveal(monitor);
                Func<AE> button=()=>Buttons(DockAt(monitor),"Dock.App").SingleOrDefault(b=>b.Current.Name==app.Name);
                Until(()=>HasIndicator(button()),"one grouped Dock icon with a visible running indicator");
                if(!existed.Contains(target))
                {
                    ShowWindowAsync(target,6);
                    Until(()=>IsIconic(target),"new Store test window minimized");
                    Reveal(monitor);
                    var restore = WaitFor(button,"Calculator button available after minimizing its window");
                    ((InvokePattern)restore.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                    if(platform.GetWindows().Count(w=>String.Equals(w.AppId,id,StringComparison.OrdinalIgnoreCase))>1)
                    {
                        string title=platform.GetWindows().First(w=>w.Handle==target).Title;
                        Click(WaitFor(()=>Menu(title),"Store group window selection"),false);
                    }
                    Until(()=>GetForegroundWindow()==target && !IsIconic(target),"Dock restores same packaged window");
                }
            }
            finally
            {
                if(target!=IntPtr.Zero && !existed.Contains(target) && Owner(target)==capturedOwner)
                {
                    PostMessage(target,0x10,IntPtr.Zero,IntPtr.Zero);
                    Until(()=>!platform.GetWindows().Any(w=>w.Handle==target),"only newly launched Calculator window closed");
                }
            }
        }
    }
    static void Preview(string launchName,bool storeCalculator)
    {
        Start();
        var pad = OpenLaunchpad();
        var search = Find(pad, AE.AutomationIdProperty, "Search");
        Until(() => search.Current.HasKeyboardFocus, "search receives initial keyboard focus");
        foreach(string category in new[]{"Pinned","Utilities","Media","Open","All"})
        {
            Click(Find(pad,AE.AutomationIdProperty,"Launchpad.Category."+category),false);
            Until(()=>Find(pad,AE.AutomationIdProperty,"Launchpad.Category."+category).Current.ItemStatus=="Выбрано",
                "Launchpad applies category "+category);
        }
        Click(Find(pad,AE.AutomationIdProperty,"Launchpad.More"),false);
        Until(()=>Menu("Параметры Windows")!=null && Menu("Проводник")!=null && Menu("Настройки Dock…")!=null,
            "Launchpad action menu exposes system and Dock commands");
        WaitFor(()=>Roots().Select(r=>Find(r,AE.AutomationIdProperty,"Launchpad.Action.Power"))
            .FirstOrDefault(item=>item!=null && !item.Current.IsOffscreen),
            "power command is present in Launchpad actions");
        WaitFor(()=>Roots().Select(r=>Find(r,AE.AutomationIdProperty,"Launchpad.Action.Files"))
            .FirstOrDefault(item=>item!=null && !item.Current.IsOffscreen),
            "file search is present in Launchpad actions");
        Keys("{ESC}");
        Until(()=>Window("Launchpad")!=null && Menu("Параметры Windows")==null,"Esc closes only the action menu");
        search.SetFocus();
        if(Find(pad,AE.AutomationIdProperty,"Launchpad.Next")!=null)
        {
            string[] pageOne = Tiles(pad).Select(t => t.Current.Name).ToArray();
            Keys("{RIGHT "+pageOne.Length+"}");
            Until(()=>!Tiles(pad).Select(t=>t.Current.Name).SequenceEqual(pageOne),"arrow selection traverses the page boundary");
            Keys("{PGUP}");
            Until(()=>Tiles(pad).Select(t=>t.Current.Name).SequenceEqual(pageOne),"Page Up returns to the first page");
        }
        Keys("lunaris-no-match-97531");
        Until(() => Buttons(pad,"Launchpad.App").Length == 0, "type-ahead filters the grid to zero matches");
        WaitFor(()=>{ var files=Find(pad,AE.AutomationIdProperty,"Launchpad.FindFiles"); return files!=null && !files.Current.IsOffscreen ? files : null; },
            "empty search offers a visible Explorer file-search action");
        Keys("{ESC}");
        Until(() => Window("Launchpad") == null, "Esc dismisses Launchpad");

        Command("--toggle");
        pad = WaitFor(() => Window("Launchpad"), "toggle IPC opens Launchpad");
        var namesAtDock = ButtonsHiddenOrCatalogNames();
        var tile = OnPages(pad,x=>x.Current.AutomationId=="Launchpad.App" && !namesAtDock.Contains(x.Current.Name));
        if (tile == null) throw new Exception("No unpinned application in Start root.");
        string name = tile.Current.Name;
        Click(tile, true);
        var pin = WaitFor(() => Menu("Закрепить в Dock"), "pin command on a catalog entry");
        Click(pin, false);
        Command("--toggle");
        Until(()=>Window("Launchpad")==null,"toggle IPC closes Launchpad");
        if(Screen.AllScreens.Length>1) SelectMonitor(Screen.PrimaryScreen,"Monitor.all");
        var bar = Reveal(Screen.PrimaryScreen);
        var buttons = Buttons(bar, "Dock.App");
        var source = buttons.First(x => x.Current.Name == name);
        var target = buttons.First(x => x.Current.Name != name);
        var before = SettingsStore.Load().Pins.ToList();
        var original=Center(source,.5);
        Drag(source,target,true,before);
        Until(()=>before.SequenceEqual(SettingsStore.Load().Pins) && Math.Abs(Center(source,.5).X-original.X)<2,
              "Escape cancels a staged reorder and preserves settings");
        Drag(source,target,false,before);
        Until(() => !before.SequenceEqual(SettingsStore.Load().Pins), "drag changes persisted order");
        var order = SettingsStore.Load().Pins.ToList();
        Stop(); Start();
        Until(() => Buttons(Window("Dock"),"Dock.App").FirstOrDefault() != null &&
                    Buttons(Window("Dock"),"Dock.App")[0].Current.Name == name && order.SequenceEqual(SettingsStore.Load().Pins),
              "pinned item and order survive restart");
        VerifyScreens(name);
        Click(Buttons(Window("Dock"),"Dock.App")[0],true);
        Click(WaitFor(() => Menu("Открепить от Dock"), "unpin command"),false);
        Until(() => SettingsStore.Load().Pins.Count == order.Count-1, "unpin persisted");
        CheckWindows();
        CheckSettings();
        CheckLibrary();
        if (!String.IsNullOrWhiteSpace(launchName))
            foreach (var monitor in Screen.AllScreens.OrderByDescending(s => s.Primary).Take(2))
                CheckCharacterMap(monitor,launchName);
        if(storeCalculator) StoreCalculator();
        Stop();
    }
    static void CheckCharacterMap(Screen monitor, string launchName)
    {
        int[] beforeLaunch = Process.GetProcessesByName("charmap").Select(p => p.Id).ToArray();
        var pad = OpenLaunchpad(monitor);
        var search = Find(pad, AE.AutomationIdProperty, "Search");
        ((ValuePattern)search.GetCurrentPattern(ValuePattern.Pattern)).SetValue(launchName);
        var result = WaitFor(() => Buttons(pad,"Launchpad.App").SingleOrDefault(t =>
            String.Equals(t.Current.Name,launchName,StringComparison.CurrentCultureIgnoreCase)), "selected launch target");
        if (monitor.Primary)
        {
            Keys("{DOWN}");
            Keys("{ENTER}");
        }
        else Click(result,false);
        Process launched = null;
        try
        {
            Until(delegate {
                var candidate = Process.GetProcessesByName("charmap").FirstOrDefault(p => !beforeLaunch.Contains(p.Id));
                if (candidate != null) launched = candidate;
                return launched != null && Owner(GetForegroundWindow()) == launched.Id && Window("Launchpad") == null;
            }, (monitor.Primary ? "Enter" : "Click on secondary display") + " launches a new Character Map process and activates its window");
        }
        finally
        {
            if (launched != null && !launched.HasExited) { launched.CloseMainWindow(); launched.WaitForExit(3000); }
        }
    }
    static string[] ButtonsHiddenOrCatalogNames()
    {
        using (var platform = new PlatformServices(true))
        {
            var settings = SettingsStore.Load();
            var names = platform.GetApplications().Where(a => settings.Pins.Contains(a.Id)).Select(a => a.Name);
            return names.ToArray();
        }
    }
    [STAThread]
    public static int Main(string[] args)
    {
        string profile = Path.Combine(Path.GetTempPath(), "Lunaris-Preview-" + Guid.NewGuid().ToString("N"));
        int result=0;
        try
        {
            SetProcessDpiAwarenessContext(new IntPtr(-4));
            if(args.Length==2 && args[0]=="--fixtures") { Fixtures(args[1]); return 0; }
            Environment.SetEnvironmentVariable("LUNARIS_PROFILE", profile);
            exe=args[0];
            string imageArg=args.FirstOrDefault(arg=>arg.StartsWith("--folder-image=",StringComparison.Ordinal));
            if(imageArg!=null) folderImage=Path.GetFullPath(imageArg.Substring("--folder-image=".Length));
            if(args.Contains("--settings-only")) { Start(); CheckSettings(); }
            else if(args.Contains("--library-only")) { Start(); CheckLibrary(); }
            else if(args.Contains("--store-only")) {
                var cursor = Cursor.Position;
                var foreground = GetForegroundWindow();
                try {
                    SettingsStore.Save(new UserSettings { Monitor = "all" });
                    Start();
                    StoreCalculator(Screen.AllScreens.FirstOrDefault(s => !s.Primary) ?? Screen.PrimaryScreen);
                } finally {
                    Stop();
                    SetCursorPos(cursor.X,cursor.Y);
                    if (foreground != IntPtr.Zero) SetForegroundWindow(foreground);
                }
            }
            else Preview(args.Skip(1).FirstOrDefault(a=>!a.StartsWith("--",StringComparison.Ordinal)),args.Contains("--store-calculator"));
        }
        catch (Exception ex)
        {
            // Windows PowerShell 5.1 can terminate a native pipeline on its first
            // stderr line before this process has closed the isolated preview.
            Console.WriteLine("FAIL " + ex);
            Console.WriteLine("Profile: " + profile); result=1;
        }
        finally
        {
            try { Stop(); }
            catch(Exception ex) {
                Console.WriteLine("FAIL preview cleanup: " + ex);
                result=1;
                if(dock!=null && !dock.HasExited) { dock.Kill(); dock.WaitForExit(5000); }
            }
        }
        return result;
    }
}
