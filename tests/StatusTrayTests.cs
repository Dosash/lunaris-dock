using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;
using AE = System.Windows.Automation.AutomationElement;

internal static partial class PreviewTests
{
    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
    delegate void ShellEvent(IntPtr hook,uint evt,IntPtr hwnd,int objectId,int childId,uint thread,uint time);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint first,uint last,IntPtr module,ShellEvent callback,uint pid,uint thread,uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    static string ShellClass(IntPtr window)
    {
        var text=new StringBuilder(128);
        GetClassName(window,text,text.Capacity); return text.ToString();
    }
    static IntPtr ExplorerTaskbar()
    {
        int explorer=Owner(GetShellWindow());
        IntPtr shell=IntPtr.Zero;
        EnumWindows(delegate(IntPtr hwnd,IntPtr state) {
            if(Owner(hwnd)==explorer && ShellClass(hwnd)=="Shell_TrayWnd") shell=hwnd;
            return shell==IntPtr.Zero;
        },IntPtr.Zero);
        return shell;
    }

    // Out-of-context events are delivered on the registering thread. Keep that
    // thread pumping even during blocking UIA, menu tracking and image sampling.
    sealed class ShellVisibilityWatch : IDisposable
    {
        readonly Dictionary<IntPtr,string> tracked = new Dictionary<IntPtr,string>();
        readonly HashSet<string> classes = new HashSet<string> {
            "NotifyIconOverflowWindow","TopLevelWindowForOverflowXamlIsland"
        };
        readonly ManualResetEvent ready = new ManualResetEvent(false);
        readonly Thread worker;
        readonly uint pid;
        System.Windows.Forms.Control signal;
        string failure;
        int shows;
        internal ShellVisibilityWatch(bool taskbars)
        {
            if(taskbars) { classes.Add("Shell_TrayWnd"); classes.Add("Shell_SecondaryTrayWnd"); }
            pid=(uint)Owner(GetShellWindow());
            if(pid==0) throw new Exception("Explorer shell is unavailable.");
            worker=new Thread(Work) { IsBackground=true, Name="Explorer visibility observer" };
            worker.SetApartmentState(ApartmentState.STA); worker.Start();
            ready.WaitOne();
            if(failure!=null) { Dispose(); }
        }
        void Sample()
        {
            EnumWindows(delegate(IntPtr hwnd,IntPtr param) {
                if(Owner(hwnd)!=pid) return true;
                string cls=ShellClass(hwnd);
                if(cls.Length!=0) tracked[hwnd]=cls;
                if(classes.Contains(cls) && IsWindowVisible(hwnd)) failure="Visible Explorer window: "+cls;
                return true;
            },IntPtr.Zero);
        }
        void Observe(IntPtr hook,uint evt,IntPtr hwnd,int obj,int child,uint thread,uint time)
        {
            if(obj!=0 || child!=0 || hwnd==IntPtr.Zero) return;
            if(evt==0x8001) { tracked.Remove(hwnd); return; }
            string cls;
            if(!tracked.TryGetValue(hwnd,out cls)) {
                cls=ShellClass(hwnd);
                if(cls.Length==0) {
                    if(evt==0x8002 && failure==null) failure="An Explorer SHOW event could not be classified after HWND destruction";
                    return;
                }
                tracked[hwnd]=cls;
            }
            // Never discard SHOW because the window is already hidden on delivery.
            if(evt==0x8002 && classes.Contains(cls)) { shows++; failure="Explorer EVENT_OBJECT_SHOW: "+cls+" at tick "+time; }
        }
        void Work()
        {
            var observer=new ShellEvent(Observe);
            IntPtr hook=IntPtr.Zero;
            try {
                using(signal=new System.Windows.Forms.Control()) {
                    var hwnd=signal.Handle;
                    hook=SetWinEventHook(0x8000,0x8002,IntPtr.Zero,observer,pid,0,0);
                    if(hook==IntPtr.Zero) throw new Exception("Cannot register Explorer SHOW observer.");
                    Sample(); ready.Set(); Application.Run();
                    Sample();
                }
            } catch(Exception ex) { failure=ex.Message; }
            finally {
                if(hook!=IntPtr.Zero) UnhookWinEvent(hook);
                GC.KeepAlive(observer); ready.Set();
            }
        }
        public void Dispose()
        {
            if(worker.IsAlive && signal!=null && signal.IsHandleCreated) {
                // Drain asynchronous accessibility notifications on the observer
                // thread after the final user gesture and its foreground change.
                Thread.Sleep(350);
                signal.BeginInvoke(new Action(Application.ExitThread));
            }
            worker.Join(); ready.Dispose();
            if(failure!=null) throw new Exception(failure+" (SHOW count "+shows+")");
            Console.WriteLine("PASS no Explorer SHOW event in the monitored interval ("+String.Join(", ",classes)+")");
        }
    }
    static bool TrayExpanded(AE bar)
    {
        string value=Control(bar,"StatusBar.Tray").Current.ItemStatus;
        return new[]{"Expanded","Развёрнуто","Ausgeklappt","Expandido","已展开"}.Contains(value);
    }
    static void SetTray(AE bar,bool expanded)
    {
        if(TrayExpanded(bar)==expanded) return;
        ClickControl(bar,"StatusBar.Tray");
        Until(()=>TrayExpanded(bar)==expanded,expanded ? "Lunaris expanded the inline tray" : "Lunaris collapsed the inline tray");
    }
    static AE TrayEntry(AE bar,StatusFixture app)
    {
        return Buttons(bar,"StatusBar.TrayItem").FirstOrDefault(item=>Visible(item) &&
            item.Current.Name.IndexOf(app.Label,StringComparison.OrdinalIgnoreCase)>=0);
    }
    static bool SameArt(AE button,uint dpi,bool changed,out string sample)
    {
        sample="fixture missing, too small or covered";
        if(!Visible(button) || Owner(WindowFromPoint(Center(button,.5)))!=dock.Id) return false;
        using(var b=CaptureIcon(button)) {
            double size=18.0*dpi/96;
            if(b.Width<size+2 || b.Height<size) return false;
            double left=(b.Width-size)/2,top=(b.Height-size)/2;
            Func<double,double,Color> at=(x,y)=>b.GetPixel((int)(left+x*size),(int)(top+y*size));
            var colors=changed ? new[]{Color.Cyan,Color.Magenta} : new[]{Color.Magenta,Color.Cyan};
            sample=String.Format("{0}x{1} pixels, patch centres {2:X6}/{3:X6}",b.Width,b.Height,
                at(.24,.21).ToArgb() & 0xffffff,at(.75,.75).ToArgb() & 0xffffff);
            // Check interiors of both patches, their separation and the holes
            // around them against the actual row directly outside the icon box.
            if(RgbDistance(at(.24,.21),colors[0])>105 || RgbDistance(at(.75,.75),colors[1])>105) return false;
            foreach(var p in new[]{new PointF(.75f,.21f),new PointF(.24f,.75f),new PointF(.5f,.5f),
                    new PointF(.05f,.5f),new PointF(.92f,.35f)}) {
                var bg=b.GetPixel(1,(int)(top+p.Y*size));
                if(RgbDistance(at(p.X,p.Y),bg)>50) {
                    sample+="; transparency mismatch at "+p;
                    return false;
                }
            }
            // Measure both coloured regions, including their edges, in pixels.
            for(int i=0;i<2;i++) {
                int minX=b.Width,maxX=-1,minY=b.Height,maxY=-1;
                for(int y=0;y<b.Height;y++) for(int x=0;x<b.Width;x++) {
                    if(RgbDistance(b.GetPixel(x,y),colors[i])>130) continue;
                    minX=Math.Min(x,minX); minY=Math.Min(y,minY); maxX=Math.Max(x,maxX); maxY=Math.Max(y,maxY);
                }
                double expectedStart=(i==0 ? 3 : 19)/32.0;
                double tolerance=Math.Max(2,size*.09);
                if(Math.Abs(minX-(left+expectedStart*size))>tolerance || Math.Abs(minY-(top+expectedStart*size))>tolerance ||
                    Math.Abs(maxX-minX+1-size*10/32)>tolerance || Math.Abs(maxY-minY+1-size*(i==0 ? 8 : 10)/32)>tolerance) {
                    sample+=String.Format("; patch {0} bounds {1},{2}..{3},{4}",i,minX,minY,maxX,maxY);
                    return false;
                }
            }
            return true;
        }
    }
    static void WaitForArt(AE bar,StatusFixture app,uint dpi,bool changed)
    {
        string sample=null;
        try {
            Until(()=>SameArt(TrayEntry(bar,app),dpi,changed,out sample),
                (changed ? "changed in-place fixture" : "initial transparent fixture")+" renders with its expected geometry at "+dpi+" DPI");
        } catch(Exception ex) { throw new InvalidOperationException("Last fixture sample: "+sample,ex); }
    }
    static void CheckTrayBars(AE[] bars,bool replacement,bool requireMixedDpi,Action<AE> checkExpanded=null)
    {
        var dpis=new HashSet<uint>();
        using(var legacy=new StatusFixture())
        using(var modern=new StatusFixture(true))
        using(var watch=replacement ? null : new ShellVisibilityWatch(false)) {
            foreach(var app in new[]{legacy,modern})
            foreach(var bar in bars) {
                uint dpi=GetDpiForWindow(new IntPtr(bar.Current.NativeWindowHandle));
                if(dpi==0) throw new Exception("Cannot identify the status bar monitor DPI.");
                dpis.Add(dpi);
                bool expanded=TrayExpanded(bar);
                try {
                    SetTray(bar,false);
                    app.Read(Probe.ChangeArt,0);
                    SetTray(bar,true);
                    if(Visible(Control(bar,"StatusBar.NativeTray"))) throw new Exception("Native overflow command exposed.");
                    WaitFor(()=>TrayEntry(bar,app),"fixture icon found directly in the upper row");
                    if(checkExpanded!=null) checkExpanded(bar);
                    // Keep pointer highlighting and tooltips outside the sample.
                    var quiet=Center(bar,.08);
                    Move(quiet);
                    WaitForArt(bar,app,dpi,false);
                    app.Read(Probe.ChangeArt,1);
                    WaitForArt(bar,app,dpi,true);
                    int left=app.Read(Probe.Left), right=app.Read(Probe.Right), picked=app.Read(Probe.Command);
                    var entry=TrayEntry(bar,app);
                    var bounds=entry.Current.BoundingRectangle;
                    var centre=Center(entry,.5);
                    Click(entry,false);
                    Until(()=>app.Read(Probe.Left)==left+1,"fixture received the ordinary left click exactly once");
                    if(app.Read(Probe.Right)!=right || app.Read(Probe.MenuX)!=Int32.MinValue)
                        throw new Exception("Left click invoked the context action.");
                    if(app==modern) CheckNativeAnchor(app,centre,bounds);
                    Click(TrayEntry(bar,app),true);
                    Until(()=>app.Read(Probe.MenuX)!=Int32.MinValue,"fixture application's own context menu opened");
                    if(app==modern) CheckNativeAnchor(app,centre,bounds);
                    app.ClickOwned(new Point(app.Read(Probe.MenuX),app.Read(Probe.MenuY)));
                    Until(()=>app.Read(Probe.Command)==picked+1 && app.Read(Probe.MenuX)==Int32.MinValue,
                        "physical selection reached the fixture application's context-menu command");
                    if(app.Read(Probe.Left)!=left+1 || app.Read(Probe.Right)!=right+1)
                        throw new Exception("Tray gesture was duplicated or delivered as the wrong button.");
                    SetTray(bar,false);
                    Until(()=>!Visible(TrayEntry(bar,app)),"fixture is absent from the collapsed row");
                } finally { SetTray(bar,expanded); }
            }
        }
        if(dpis.Count>=2) Console.WriteLine("PASS physical icon rendering on mixed-DPI monitors: "+String.Join(", ",dpis));
        else if(requireMixedDpi) throw new Exception("Two distinct status-bar monitor DPIs are required.");
        else if(replacement) Console.WriteLine("SKIP mixed-DPI comparison: only one monitor DPI tested ("+String.Join(", ",dpis)+")");
    }

    static void CheckNativeAnchor(StatusFixture app,Point click,System.Windows.Rect button)
    {
        var anchor=new Point(app.Read(Probe.AnchorX),app.Read(Probe.AnchorY));
        var bounds=Rectangle.FromLTRB(app.Read(Probe.RectLeft),app.Read(Probe.RectTop),
            app.Read(Probe.RectRight),app.Read(Probe.RectBottom));
        int hr=app.Read(Probe.RectResult);
        if(Math.Abs(click.X-anchor.X)>1 || Math.Abs(click.Y-anchor.Y)>1)
            throw new Exception("V4 callback coordinates "+anchor+" differ from the physical click "+click);
        if(hr<0 || !bounds.Contains(click) || bounds.Width<4 || bounds.Height<4 ||
            bounds.Left<button.Left-2 || bounds.Top<button.Top-2 || bounds.Right>button.Right+2 || bounds.Bottom>button.Bottom+2)
            throw new Exception(String.Format("V4 Shell_NotifyIconGetRect returned 0x{0:X8}, {1} for button {2}",hr,bounds,button));
        Console.WriteLine("PASS V4 GUID icon received its own ID, physical click point and matching shell anchor rectangle");
    }
}
