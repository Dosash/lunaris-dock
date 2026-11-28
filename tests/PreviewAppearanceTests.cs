using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;
using Lunaris;
using AE = System.Windows.Automation.AutomationElement;

internal static partial class PreviewTests
{
    [DllImport("user32.dll", EntryPoint="SystemParametersInfoW")]
    static extern bool GetWorkArea(uint action, uint param, out NativeRect area, uint flags);

    static Rectangle PrimaryWorkArea()
    {
        NativeRect area;
        if (!GetWorkArea(0x0030,0,out area,0)) throw new Exception("Cannot read the primary work area.");
        return Rectangle.FromLTRB(area.Left,area.Top,area.Right,area.Bottom);
    }

    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd,StringBuilder name,int size);

    static IntPtr VisibleNativeOverflow()
    {
        foreach(string cls in new[]{"TopLevelWindowForOverflowXamlIsland","NotifyIconOverflowWindow"})
        {
            var hwnd=FindWindow(cls,null);
            if(hwnd!=IntPtr.Zero && IsWindowVisible(hwnd)) return hwnd;
        }
        return IntPtr.Zero;
    }

    static AE Control(AE window, string id)
    {
        return Find(window, AE.AutomationIdProperty, id);
    }

    static bool Visible(AE element)
    {
        return element != null && !element.Current.IsOffscreen && !element.Current.BoundingRectangle.IsEmpty;
    }

    static AE EditorWindow()
    {
        foreach(var window in Roots()) {
            if(window.Current.AutomationId=="IconEditor" && Visible(window)) return window;
            // Start owns the modal composer. Depending on the provider an owned
            // window is a descendant of the owner instead of a desktop child.
            var owned=Control(window,"IconEditor");
            if(Visible(owned)) return owned;
        }
        return null;
    }

    static void ClickControl(AE window, string id)
    {
        Click(WaitFor(()=>Control(window,id),"found " + id),false);
    }

    static void CheckAppearance()
    {
        var cursor = Cursor.Position;
        var foreground = GetForegroundWindow();
        var workArea=PrimaryWorkArea();
        var settings = SettingsStore.Load();
        settings.InitializePins = false;
        settings.HideDockWhenCovered = false;
        var library = new LaunchLibrary(settings, new AppEntry[0]);
        string folder = library.CreateFolder("Lunaris Icon QA",LaunchLibrary.Root);
        settings.Pins.Add("dock-folder:" + folder);
        settings.Pins.Add(Path.Combine(Environment.SystemDirectory,"charmap.exe"));
        settings.Pins.Add("dock-directory:" + SettingsStore.ProfileDirectory);
        SettingsStore.Save(settings);
        try
        {
            Start();
            if(PrimaryWorkArea()!=workArea) throw new Exception("Preview changed the Windows work area.");
            CheckIconEditor(folder);
            CheckStatusStrip();
        }
        finally
        {
            Stop();
            SetCursorPos(cursor.X,cursor.Y);
            if (foreground != IntPtr.Zero) SetForegroundWindow(foreground);
            if(PrimaryWorkArea()!=workArea) throw new Exception("Preview left a work-area reservation after exit.");
        }
    }

    static void CheckIconEditor(string folder)
    {
        var dockWindow = Reveal(Screen.PrimaryScreen);
        Func<AE> folderButton = ()=>Buttons(Window("Dock"),"Dock.Folder").Single(b=>b.Current.Name=="Lunaris Icon QA");
        ClickEditTarget(Control(dockWindow,"OpenLaunchpad"),true,"Start button");
        Click(Action("Icons.EditMode"),false);
        CheckLeftEdit(Control(dockWindow,"OpenLaunchpad"),"Start button");
        CheckLeftEdit(Buttons(dockWindow,"Dock.App")[0],"pinned application");
        CheckLeftEdit(Buttons(dockWindow,"Dock.Folder").Single(b=>b.Current.Name!= "Lunaris Icon QA"),
            "pinned Explorer directory");
        Command("--toggle");
        var libraryWindow=WaitFor(()=>Window("Launchpad"),"open Start in editing mode");
        var tile=WaitFor(()=>Buttons(libraryWindow,"Launchpad.App").FirstOrDefault(),"application tile in Start");
        CheckLeftEdit(tile,"Start application tile");
        Until(()=>Window("Launchpad")!=null,"Start stays open when the editor is cancelled");
        Keys("{ESC}");
        Until(()=>Window("Launchpad")==null,"return to the Dock");
        Click(folderButton(),false);
        var editor = WaitFor(EditorWindow,"left-click on a folder in editing mode opens the icon composer");
        ClickControl(editor,"IconEditor.Glyph.star");
        var color = Control(editor,"IconEditor.BaseColor");
        ((ValuePattern)color.GetCurrentPattern(ValuePattern.Pattern)).SetValue("#CC134C");
        ClickControl(editor,"IconEditor.Save");
        Until(()=>EditorWindow()==null && SettingsStore.Load().IconDesigns.Any(d=>d.Key==folder &&
            d.Glyph=="star" && String.Equals(d.BaseColor,"#CC134C",StringComparison.OrdinalIgnoreCase)),
            "saved composer settings belong to the folder's persistent identity");
        Until(delegate {
            using(var art=CaptureIcon(folderButton()))
            {
                int red=0;
                for(int y=0;y<art.Height;y++) for(int x=0;x<art.Width;x++) {
                    Color c=art.GetPixel(x,y);
                    if (c.R>140 && c.R>c.G+60 && c.R>c.B+45) red++;
                }
                return red>30;
            }
        }, "saved custom folder colour is visible on the actual Dock");
        Click(folderButton(),false);
        editor = WaitFor(EditorWindow,"same folder can be edited again");
        ClickControl(editor,"IconEditor.Reset");
        ClickControl(editor,"IconEditor.Cancel");
        Until(()=>EditorWindow()==null && SettingsStore.Load().IconDesigns.Any(d=>d.Key==folder),
            "cancelling a reset preserves the previously saved drawing");
        Stop(); Start();
        Click(folderButton(),false);
        var pad=WaitFor(()=>Window("Launchpad"),"ordinary left-click opens the pinned folder after restart");
        Until(()=>Control(pad,"Launchpad.Title").Current.Name=="Lunaris Icon QA" &&
            SettingsStore.Load().IconDesigns.Any(d=>d.Key==folder), "folder identity and custom icon survive reopening");
        Keys("{ESC}");
        Until(()=>Window("Launchpad")==null,"closed folder view");
        ClickEditTarget(folderButton(),true,"pinned folder");
        Click(Action("Icons.Edit"),false);
        editor=WaitFor(EditorWindow,"direct icon editing command opens the saved design");
        ClickControl(editor,"IconEditor.Reset");
        ClickControl(editor,"IconEditor.Save");
        Until(()=>EditorWindow()==null && !SettingsStore.Load().IconDesigns.Any(d=>d.Key==folder),
            "saving a reset restores the original artwork");
    }

    static void ClickEditTarget(AE target, bool right, string label)
    {
        // Hover magnification and a pending Dock render can move or replace the
        // button after the previous editor closes. Resolve its identity again
        // and verify the physical hit target before sending the single click.
        string id=target.Current.AutomationId, name=target.Current.Name;
        Until(delegate {
            var root=Window(id.StartsWith("Launchpad.") ? "Launchpad" : "Dock");
            var button=Buttons(root,id).FirstOrDefault(b=>b.Current.Name==name);
            if(!Visible(button)) return false;
            var p=Center(button,.5);
            Move(p); Thread.Sleep(120);
            var hit=AE.FromPoint(new System.Windows.Point(p.X,p.Y));
            while(hit!=null && hit.Current.ProcessId==dock.Id) {
                if(Automation.Compare(hit,button)) return Cursor.Position==p;
                hit=TreeWalker.RawViewWalker.GetParent(hit);
            }
            return false;
        },"pointer is over the " + label);
        mouse_event(right ? 8u : 2u,0,0,0,UIntPtr.Zero);
        mouse_event(right ? 16u : 4u,0,0,0,UIntPtr.Zero);
    }

    static void CheckLeftEdit(AE target,string label)
    {
        ClickEditTarget(target,false,label);
        var editor=WaitFor(EditorWindow,"left-click edits the "+label);
        ClickControl(editor,"IconEditor.Cancel");
        Until(()=>EditorWindow()==null,"cancel "+label+" edit");
    }

    static int StripBrightness()
    {
        var bar=Window("StatusBar");
        if (!Visible(bar)) throw new Exception("Status bar is hidden during theme comparison.");
        var point=Center(bar,.35);
        if(Owner(WindowFromPoint(point))!=dock.Id) throw new Exception("Status bar sample is covered.");
        using(var pixel=new Bitmap(1,1))
        using(var graphics=Graphics.FromImage(pixel))
        {
            graphics.CopyFromScreen(point,Point.Empty,pixel.Size);
            Color c=pixel.GetPixel(0,0);
            return (c.R+c.G+c.B)/3;
        }
    }

    static void CheckMenuMaterial(AE bar,string theme)
    {
        var menu=OpenMenu(bar,"Sound");
        try {
            Until(delegate {
                var r=menu.Current.BoundingRectangle;
                if(r.IsEmpty) return false;
                var top=new Point((int)(r.Left+.97*r.Width),(int)(r.Top+.11*r.Height));
                var lower=new Point(top.X,(int)(r.Top+.55*r.Height));
                if(Owner(WindowFromPoint(top))!=dock.Id || Owner(WindowFromPoint(lower))!=dock.Id) return false;
                using(var pixels=new Bitmap(2,1))
                using(var g=Graphics.FromImage(pixels)) {
                    g.CopyFromScreen(top,Point.Empty,new Size(1,1));
                    g.CopyFromScreen(lower,new Point(1,0),new Size(1,1));
                    Color a=pixels.GetPixel(0,0), b=pixels.GetPixel(1,0);
                    int brightness=(a.R+a.G+a.B)/3;
                    return theme=="glass" ? RgbDistance(a,b)>18 : theme=="light" ? brightness>170 : brightness<120;
                }
            },"Lunaris popup renders "+theme+" material on screen");
        } finally { EscapeMenu("Sound"); }
    }

    static void CheckStatusStrip()
    {
        var bar=WaitFor(()=>Window("StatusBar"),"status bar appears on preview startup");
        if(Control(bar,"StatusBar.Start")!=null) throw new Exception("Unexpected Start button in the upper status bar.");
        var area=PrimaryWorkArea();
        var bounds=bar.Current.BoundingRectangle;
        if(Math.Abs(bounds.Top-area.Top)>2 || Math.Abs(bounds.Width-area.Width)>4)
            throw new Exception("Status bar is not aligned with the top of the monitor.");
        foreach(string id in new[]{"Clock","Notifications","Network","Sound","Language","Tray"})
        {
            var button=Control(bar,"StatusBar."+id);
            if(!Visible(button) || !button.Current.IsEnabled || button.Current.BoundingRectangle.Left < bounds.Left + bounds.Width*.5)
                throw new Exception("Missing right-hand status control " + id);
        }
        Until(delegate {
            string time=DateTime.Now.ToString("t",CultureInfo.CurrentCulture);
            return Control(bar,"StatusBar.Clock").Current.Name.Contains(time);
        },"status clock displays the current local time");
        CheckShellFlyout(bar,"Notifications");
        CheckStatusMenus(bar,"Menus");
        var prefs=OpenSettings(Screen.PrimaryScreen);
        ChooseTheme(prefs,"Light");
        Until(()=>StripBrightness()>170,"light theme applied to the open status bar");
        CheckTrayBars(new[]{bar},false,false);
        CheckMenuMaterial(bar,"light");
        ChooseTheme(prefs,"Dark");
        Until(()=>StripBrightness()<120,"dark theme applied to the same bar");
        CheckMenuMaterial(bar,"dark");
        Toggle(prefs,"LiquidGlass",true);
        Until(()=>SettingsStore.Load().LiquidGlass && Visible(Window("StatusBar")),"Liquid Glass applies with the bar visible");
        CheckMenuMaterial(bar,"glass");
        Toggle(prefs,"ShowStatusBar",false);
        Until(()=>!SettingsStore.Load().ShowStatusBar && Window("StatusBar")==null && Window("Settings")!=null,
            "hiding the status bar takes effect while settings remain open");
        CloseSettings(prefs);
        Stop(); Start();
        Until(()=>Window("StatusBar")==null && !SettingsStore.Load().ShowStatusBar,"hidden preference survives restart");
        prefs=OpenSettings(Screen.PrimaryScreen);
        Toggle(prefs,"ShowStatusBar",true);
        WaitFor(()=>Window("StatusBar"),"status bar reappears when re-enabled");
        CloseSettings(prefs);
    }

    static bool ShellFlyout(IntPtr hwnd)
    {
        if(hwnd==IntPtr.Zero || !IsWindowVisible(hwnd)) return false;
        int pid=Owner(hwnd);
        if(pid==0) return false;
        try
        {
            using(var process=Process.GetProcessById(pid))
            {
                if(process.ProcessName=="ShellExperienceHost" || process.ProcessName=="ShellHost") return true;
                if(process.ProcessName!="explorer") return false;
            }
        }
        catch(ArgumentException) { return false; }
        var cls=new StringBuilder(128);
        GetClassName(hwnd,cls,cls.Capacity);
        return cls.ToString()=="ControlCenterWindow" || cls.ToString()=="Windows.UI.Core.CoreWindow" ||
            cls.ToString()=="XamlExplorerHostIslandWindow";
    }

    static void CheckShellFlyout(AE bar, string control)
    {
        ClickControl(bar,"StatusBar."+control);
        IntPtr opened=IntPtr.Zero;
        Until(delegate {
            opened=GetForegroundWindow();
            return ShellFlyout(opened);
        },control+" opens a Windows shell flyout");
        // The window is owned by the shell. Inspect no notifications, and change no settings.
        if(GetForegroundWindow()==opened) SendKeys.SendWait("{ESC}");
        Until(()=>GetForegroundWindow()!=opened || !IsWindowVisible(opened),control+" flyout closed");
    }

}
