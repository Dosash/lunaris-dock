using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;
using AE = System.Windows.Automation.AutomationElement;

internal static partial class PreviewTests
{
    [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] static extern int GetKeyboardLayoutList(int size,[Out] IntPtr[] list);

    static AE StatusMenu(string name)
    {
        string id="StatusMenu."+name;
        foreach(var root in Roots()) {
            var menu=root.Current.AutomationId==id ? root : Control(root,id);
            if(Visible(menu)) return menu;
        }
        return null;
    }
    static AE OpenMenu(AE bar,string name)
    {
        ClickControl(bar,"StatusBar."+name);
        var menu=WaitFor(()=>StatusMenu(name),"Lunaris "+name+" menu opened");
        if(menu.Current.ProcessId!=dock.Id || ShellFlyout(GetForegroundWindow()))
            throw new Exception("Lunaris does not own the active "+name+" menu.");
        return menu;
    }
    static void EscapeMenu(string name)
    {
        if(StatusMenu(name)==null) return;
        Keys("{ESC}");
        Until(()=>StatusMenu(name)==null,"Escape closed the Lunaris "+name+" menu");
    }

    // Independent Core Audio endpoint, kept alive for exact scalar/mute restoration.
    [ComImport,Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITestDevices {
        void EnumAudioEndpoints(int flow,int state,out ITestCollection list);
        void GetDefaultAudioEndpoint(int flow,int role,out ITestDevice device);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)]string id,out ITestDevice device);
    }
    [ComImport,Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITestCollection { void GetCount(out uint count); void Item(uint index,out ITestDevice device); }
    [ComImport,Guid("D666063F-1587-4E43-81F1-B948E807363F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITestDevice {
        void Activate(ref Guid iid,uint cls,IntPtr args,[MarshalAs(UnmanagedType.IUnknown)]out object result);
        void OpenStore(); void GetId([MarshalAs(UnmanagedType.LPWStr)]out string id);
    }
    [ComImport,Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITestVolume {
        void Register(); void Unregister(); void GetChannelCount(); void SetDb();
        void SetScalar(float value,ref Guid context); void GetDb(); void GetScalar(out float value);
        void SetChannelDb(); void SetChannelScalar(); void GetChannelDb(); void GetChannelScalar();
        void SetMuted([MarshalAs(UnmanagedType.Bool)]bool value,ref Guid context);
        void GetMuted([MarshalAs(UnmanagedType.Bool)]out bool value);
    }
    static void TestAudio(AE bar)
    {
        object enumerator=null,endpoint=null;
        ITestDevice device=null;
        try {
            enumerator=Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"),true));
            ((ITestDevices)enumerator).GetDefaultAudioEndpoint(0,1,out device);
            var iid=typeof(ITestVolume).GUID;
            device.Activate(ref iid,1,IntPtr.Zero,out endpoint);
            var audio=(ITestVolume)endpoint;
            float original; bool muted; audio.GetScalar(out original); audio.GetMuted(out muted);
            try {
                var menu=OpenMenu(bar,"Sound");
                var slider=WaitFor(()=>Control(menu,"StatusMenu.Volume"),"Lunaris volume slider is accessible");
                var range=(RangeValuePattern)slider.GetCurrentPattern(RangeValuePattern.Pattern);
                if(Math.Abs(range.Current.Value-original*100)>2) throw new Exception("Menu volume disagrees with the endpoint.");
                var toggle=Control(menu,"StatusMenu.Mute");
                var togglePattern=(TogglePattern)toggle.GetCurrentPattern(TogglePattern.Pattern);
                if((togglePattern.Current.ToggleState==ToggleState.On)!=muted) throw new Exception("Menu mute disagrees with the endpoint.");
                int goal=(int)Math.Round(original*100)+(original>=.08f ? -6 : 6);
                range.SetValue(goal);
                Until(delegate { float actual; audio.GetScalar(out actual); return Math.Abs(goal-actual*100)<2; },
                    "changing the slider updates the actual Core Audio endpoint");
                var thumb=WaitFor(()=>slider.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AE.ControlTypeProperty,ControlType.Thumb)),"volume thumb has a drag target");
                var grip=Center(thumb,.5);
                Move(grip);
                mouse_event(2,0,0,0,UIntPtr.Zero);
                try {
                    Thread.Sleep(65);
                    SetCursorPos(grip.X+(goal<50 ? 25 : -25),grip.Y);
                    Until(delegate { float actual; audio.GetScalar(out actual); return Math.Abs(goal-actual*100)>2; },
                        "dragging the custom thumb changes the endpoint volume");
                } finally { mouse_event(4,0,0,0,UIntPtr.Zero); }
                bool beforeClick; audio.GetMuted(out beforeClick);
                Move(Center(toggle,.035)); mouse_event(2,0,0,0,UIntPtr.Zero); mouse_event(4,0,0,0,UIntPtr.Zero);
                Until(delegate { bool current; audio.GetMuted(out current); return current!=beforeClick; },"mute click changes endpoint mute");
            } finally {
                try { EscapeMenu("Sound"); }
                finally {
                    var context=Guid.Empty;
                    audio.SetScalar(original,ref context); audio.SetMuted(muted,ref context);
                    Until(delegate { float current; bool m; audio.GetScalar(out current); audio.GetMuted(out m);
                        return Math.Abs(current-original)<0.00001 && m==muted; },"original scalar volume and mute restored");
                }
            }
        } finally {
            if(endpoint!=null) Marshal.ReleaseComObject(endpoint);
            if(device!=null) Marshal.ReleaseComObject(device);
            if(enumerator!=null) Marshal.ReleaseComObject(enumerator);
        }
    }
    static string[] ActiveNetworkIds()
    {
        return NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up &&
            n.NetworkInterfaceType!=NetworkInterfaceType.Loopback).Select(n=>n.Id).OrderBy(s=>s).ToArray();
    }
    static void TestNetwork(AE bar,StatusFixture app)
    {
        var active=ActiveNetworkIds();
        try {
            var menu=OpenMenu(bar,"Network");
            foreach(string kind in new[]{"Ethernet","Wifi"}) {
                Until(delegate { var e=Control(menu,"StatusMenu."+kind);
                    return Visible(e) && !String.IsNullOrWhiteSpace(e.Current.Name); },"network menu shows "+kind+" status");
                var text=Control(menu,"StatusMenu."+kind).Current.Name.Split('·').Last().Trim();
                var online=NetworkInterface.GetAllNetworkInterfaces().Any(n=>n.OperationalStatus==OperationalStatus.Up &&
                    (kind=="Wifi" ? n.NetworkInterfaceType==NetworkInterfaceType.Wireless80211 :
                    n.NetworkInterfaceType.ToString().IndexOf("Ethernet",StringComparison.Ordinal)>=0));
                var expected=online ? new[]{"Connected","Подключено","Verbunden","Conectado","已连接"} :
                    new[]{"Disconnected","Отключено","Getrennt","Desconectado","未连接"};
                if(!expected.Contains(text)) throw new Exception(kind+" menu status disagrees with Windows.");
            }
            var list=Control(menu,"StatusMenu.NetworkList");
            if(!Visible(list)) throw new Exception("Missing visible wireless network list container.");
            var rows=Buttons(list,"StatusMenu.NetworkRow").Where(Visible).ToArray();
            if(rows.Length>0) {
                var connected=rows.FirstOrDefault(e=>e.Current.Name.StartsWith("✓"));
                var idle=rows.FirstOrDefault(e=>!e.Current.Name.StartsWith("✓"));
                foreach(var row in new[]{connected,idle}.Where(e=>e!=null)) {
                    bool current=row==connected;
                    var fresh=Buttons(Control(menu,"StatusMenu.NetworkList"),"StatusMenu.NetworkRow").FirstOrDefault(e=>Visible(e) && e.Current.Name.StartsWith("✓")==current);
                    if(fresh==null) continue;
                    Click(fresh,false);
                    var back=WaitFor(()=>Control(menu,"StatusMenu.Back"),"available WLAN details include a cancel command");
                    if(current && !Visible(Control(menu,"StatusMenu.Disconnect"))) throw new Exception("Missing disconnect action.");
                    if(!current && Visible(Control(menu,"StatusMenu.Disconnect"))) throw new Exception("Inactive network offers disconnect.");
                    // Inspect both detail states without issuing a radio command.
                    Click(back,false);
                    WaitFor(()=>Control(menu,"StatusMenu.Ethernet"),"wireless details return to the interface list");
                }
            } else {
                Until(delegate {
                    var state=Control(list,"StatusMenu.NetworkListState");
                    return Visible(state) && !String.IsNullOrWhiteSpace(state.Current.Name);
                },"wireless list displays a visible explanation for the absence of networks");
                Console.WriteLine("SKIP WLAN action details: no accessible wireless network rows.");
            }
            app.FocusEditor(false);
            Until(()=>StatusMenu("Network")==null,"outside click closes the Lunaris network menu");
            if(!ActiveNetworkIds().SequenceEqual(active)) throw new Exception("Active network adapters changed during the read-only menu check.");
        } finally { EscapeMenu("Network"); }
    }
    static string LayoutKey(IntPtr hkl) { return unchecked((uint)hkl.ToInt64()).ToString("X8"); }
    static void TestLayouts(AE bar,StatusFixture app)
    {
        app.FocusEditor();
        int pid; uint thread=GetWindowThreadProcessId(app.Hwnd,out pid);
        if(thread==0 || pid!=app.Process.Id) throw new Exception("Cannot identify the fixture editor's thread.");
        IntPtr original=GetKeyboardLayout(thread);
        var installed=new IntPtr[GetKeyboardLayoutList(0,null)];
        int count=GetKeyboardLayoutList(installed.Length,installed);
        if(original==IntPtr.Zero || count==0) throw new Exception("Cannot read current keyboard layouts.");
        try {
            var menu=OpenMenu(bar,"Language");
            var entries=Buttons(menu,"StatusMenu.Layout");
            if(installed.Take(count).Any(h=>!entries.Any(e=>e.Current.ItemStatus==LayoutKey(h))) ||
                !entries.Any(e=>e.Current.HelpText=="current" && e.Current.ItemStatus==LayoutKey(original)))
                throw new Exception("Menu does not report the current and installed keyboard layouts.");
            var next=installed.Take(count).FirstOrDefault(h=>h!=original);
            if(next==IntPtr.Zero) {
                Console.WriteLine("SKIP switching between distinct layouts: a single layout is installed.");
                next=original;
            }
            int chars=app.Read(Probe.InputLength);
            Click(entries.Single(e=>e.Current.ItemStatus==LayoutKey(next)),false);
            Until(()=>StatusMenu("Language")==null && GetForegroundWindow()==app.Hwnd && GetKeyboardLayout(thread)==next,
                "selected HKL applies on the editor thread and foreground returns to its window");
            SendKeys.SendWait("1");
            Until(()=>app.Read(Probe.InputLength)==chars+1,"the next keystroke reaches the original editor");
        } finally {
            try { EscapeMenu("Language"); }
            finally {
                if(GetKeyboardLayout(thread)!=original) {
                    if(!PostMessage(app.Hwnd,0x0050,IntPtr.Zero,original)) throw new Exception("Could not restore the editor layout.");
                    Until(()=>GetKeyboardLayout(thread)==original,"original editor keyboard layout restored");
                }
            }
        }
    }
    static void CheckStatusMenus(AE bar,string selected)
    {
        using(var app=new StatusFixture()) {
            app.FocusEditor();
            try {
                if(selected=="Menus" || selected=="Sound") TestAudio(bar);
                if(selected=="Menus" || selected=="Network") TestNetwork(bar,app);
                if(selected=="Menus" || selected=="Language") TestLayouts(bar,app);
            } finally {
                // Background updates can close a menu between an assertion and cleanup.
                foreach(string name in new[]{"Sound","Network","Language"}) {
                    var opened=StatusMenu(name);
                    if(opened!=null) app.FocusEditor();
                }
            }
        }
    }
}
