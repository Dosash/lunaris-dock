using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;
using Lunaris;
using Microsoft.Win32;
using AE = System.Windows.Automation.AutomationElement;

internal static partial class PreviewTests
{
    static readonly Color[] MarkerColors={Color.Fuchsia,Color.Lime,Color.Cyan,Color.Yellow};
    static void ConfigureHoverFixture()
    {
        var profile=SettingsStore.Load();
        profile.Monitor="all"; profile.Size=80; profile.HideDockWhenCovered=false;
        profile.Magnify=true; profile.InitializePins=false; profile.ShowUpdateBadge=false;
        string path; using(var me=Process.GetCurrentProcess()) path=me.MainModule.FileName;
        string folder=new LaunchLibrary(profile,new AppEntry[0]).CreateFolder("Magnification QA folder",LaunchLibrary.Root);
        profile.Pins.Clear(); profile.Pins.Add(path);
        profile.Pins.Add("dock-separator:hover-fixture");
        profile.Pins.Add("dock-folder:"+folder);
        SetHoverMarkers(profile, "lunaris:start", LaunchLibrary.AppKey(path), folder);
        SettingsStore.Save(profile);
    }
    static void SetHoverMarkers(UserSettings profile, params string[] keys)
    {
        using(var bitmap=new Bitmap(96,96,PixelFormat.Format32bppArgb))
        using(var memory=new MemoryStream())
        {
            using(var g=Graphics.FromImage(bitmap)) {
                g.Clear(Color.Transparent);
                for(int i=0;i<4;i++) using(var brush=new SolidBrush(MarkerColors[i]))
                    g.FillRectangle(brush,(i%2)*76,(i/2)*76,20,20);
            }
            bitmap.Save(memory,ImageFormat.Png);
            string png=Convert.ToBase64String(memory.ToArray());
            foreach(string key in keys) {
                profile.IconDesigns.RemoveAll(d=>String.Equals(d.Key,key,StringComparison.OrdinalIgnoreCase));
                profile.IconDesigns.Add(new IconDesign { Key=key, Shape="none", Size=100, Foreground="image", ImagePng=png });
            }
        }
    }
    static Rectangle[] MarkerBounds(Rectangle box)
    {
        var marks=new Rectangle[4];
        using(var shot=new Bitmap(box.Width,box.Height))
        using(var g=Graphics.FromImage(shot)) {
            g.CopyFromScreen(box.Location,Point.Empty,box.Size);
            for(int y=0;y<box.Height;y++) for(int x=0;x<box.Width;x++) {
                var pixel=shot.GetPixel(x,y);
                for(int i=0;i<marks.Length;i++) {
                    if(RgbDistance(pixel,MarkerColors[i])>38) continue;
                    var at=new Rectangle(box.Left+x,box.Top+y,1,1);
                    marks[i]=marks[i].IsEmpty ? at : Rectangle.Union(marks[i],at);
                }
            }
        }
        if(marks.Any(r=>r.Width<3 || r.Height<3)) throw new Exception("The Dock did not render all four corner markers.");
        return marks;
    }
    static Rectangle HoverSamplingBounds(AE bar,System.Windows.Rect b)
    {
        return Rectangle.Intersect(FixtureBounds(new IntPtr(bar.Current.NativeWindowHandle)),Rectangle.FromLTRB((int)b.Left-8,
            (int)(b.Top-b.Height*.65),(int)b.Right+8,(int)b.Bottom+8));
    }
    static void CheckHoverCorners(Screen screen)
    {
        var window=Reveal(screen);
        var hwnd=new IntPtr(window.Current.NativeWindowHandle);
        uint dpi=GetDpiForWindow(hwnd);
        foreach(string id in new[]{"OpenLaunchpad","Dock.App","Dock.Folder"}) {
            var button=WaitFor(()=>Buttons(window,id).FirstOrDefault(e=>Visible(e) && (id!="Dock.Folder" ||
                e.Current.Name=="Magnification QA folder")),"marked "+id+" on "+screen.DeviceName);
            var b=button.Current.BoundingRectangle;
            var box=HoverSamplingBounds(window,b);
            SetCursorPos((int)b.Left,(int)b.Top-(int)b.Height); Thread.Sleep(350);
            var idle=MarkerBounds(box);
            // The Dock can replace its UI Automation peers on a catalog refresh.
            // Hover the same physical rectangle from which the idle pixels were sampled.
            Move(new Point((int)(b.Left+b.Width*.5),(int)(b.Top+b.Height*.5))); Thread.Sleep(240);
            var hover=MarkerBounds(box);
            // A clipped corner can still move. Measure each patch as well as the
            // distances between them. Separators isolate adjacent marked icons.
            double sx=(hover[1].Right-hover[0].Left)/(double)(idle[1].Right-idle[0].Left);
            double sy=(hover[2].Bottom-hover[0].Top)/(double)(idle[2].Bottom-idle[0].Top);
            if(sx<1.15 || sx>1.40 || Math.Abs(sy-sx)>.05) throw new Exception(id+" clipped magnification: X="+sx+" Y="+sy);
            for(int i=0;i<4;i++) if(Math.Abs(hover[i].Height-idle[i].Height*sx)>2.25 ||
                Math.Abs(hover[i].Width-idle[i].Width*sx)>2.25)
                throw new Exception(id+" cut off corner "+i+" ("+idle[i]+" -> "+hover[i]+")");
            if(hover[0].Top>=b.Top-3*dpi/96.0) throw new Exception(id+" artwork did not extend above the plate.");
            Console.WriteLine("PASS all four enlarged "+id+" corners render beyond the plate without clipping at "+dpi+" DPI");
        }
    }
    static Type RadioType { get { return Type.GetType("Windows.Devices.Radios.Radio, Windows.System.Devices, ContentType=WindowsRuntime",true); } }
    static object WinRtResult(MethodInfo get,object target,params object[] args)
    {
        var op=get.Invoke(target,args);
        var ext=Assembly.Load("System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089")
            .GetType("System.WindowsRuntimeSystemExtensions",true);
        var asTask=ext.GetMethods().Single(m=>m.Name=="AsTask" && m.IsGenericMethodDefinition && m.GetParameters().Length==1 &&
            m.GetParameters()[0].ParameterType.Name=="IAsyncOperation`1");
        var task=(Task)asTask.MakeGenericMethod(get.ReturnType.GetGenericArguments()).Invoke(null,new[]{op});
        if(!task.Wait(8000)) throw new TimeoutException("Windows did not complete "+get.Name+".");
        return task.GetType().GetProperty("Result").GetValue(task,null);
    }
    static object[] SystemRadios()
    {
        return ((IEnumerable)WinRtResult(RadioType.GetMethod("GetRadiosAsync"),null)).Cast<object>().ToArray();
    }
    static string RadioProperty(object radio,string name)
    {
        return RadioType.GetProperty(name).GetValue(radio,null).ToString();
    }
    static string[] BluetoothStates()
    {
        return SystemRadios().Where(r=>RadioProperty(r,"Kind")=="Bluetooth")
            .Select(r=>RadioProperty(r,"State").ToLowerInvariant()).Select(s=>s=="on" || s=="off" ? s : "unavailable")
            .OrderBy(s=>s).ToArray();
    }
    static string RadioControl(AE control)
    {
        string state=control.Current.ItemStatus;
        if(!Visible(control) || String.IsNullOrWhiteSpace(control.Current.Name) ||
            control.Current.ControlType!=ControlType.CheckBox) throw new Exception("Missing checkbox or label for a radio.");
        var pattern=(TogglePattern)control.GetCurrentPattern(TogglePattern.Pattern);
        if(state=="unavailable" && !control.Current.IsEnabled) return state;
        if(!control.Current.IsEnabled || (state!="on" && state!="off") || (pattern.Current.ToggleState==ToggleState.On)!=(state=="on"))
            throw new Exception("Radio toggle state and accessible status disagree.");
        return state;
    }
    [ComImport,Guid("DB3AFBFB-08E6-46C6-AA70-BF9A34C30AB7"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITestFlight {
        void IsSupported(out int supported); void GetInstances(out IntPtr instances);
        void GetState(out int enabled,out int uiState,out int reason); void SetState(int enabled);
    }
    static bool FlightState()
    {
        object value=Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\RadioManagement\SystemRadioState","",null);
        if(!(value is int) || ((int)value!=0 && (int)value!=1)) throw new InvalidOperationException("Unknown Windows airplane state.");
        return (int)value==1;
    }
    static bool BluetoothState()
    {
        var states=Task.Run((Func<string[]>)BluetoothStates).GetAwaiter().GetResult();
        if(states.Length!=1 || states[0]=="unavailable") throw new InvalidOperationException("Bluetooth adapter is unavailable.");
        return states[0]=="on";
    }
    static int ConnectedBluetoothCount(bool lowEnergy)
    {
        string name=lowEnergy ? "BluetoothLEDevice" : "BluetoothDevice";
        var device=Type.GetType("Windows.Devices.Bluetooth."+name+", Windows.System.Devices, ContentType=WindowsRuntime",true);
        var query=device.GetMethod("GetDeviceSelectorFromConnectionStatus");
        var connected=Enum.Parse(query.GetParameters()[0].ParameterType,"Connected");
        var selector=(string)query.Invoke(null,new[]{connected});
        var info=Type.GetType("Windows.Devices.Enumeration.DeviceInformation, Windows.System.Devices, ContentType=WindowsRuntime",true);
        return ((IEnumerable)WinRtResult(info.GetMethod("FindAllAsync",new[]{typeof(string)}),null,selector)).Cast<object>().Count();
    }
    static string RadioGuard()
    {
        try {
            var nics=NetworkInterface.GetAllNetworkInterfaces();
            if(!nics.Any(n=>n.OperationalStatus==OperationalStatus.Up && n.NetworkInterfaceType.ToString().Contains("Ethernet") &&
                n.GetIPProperties().GatewayAddresses.Count>0)) return "no active Ethernet connection with a gateway";
            if(nics.Any(n=>n.NetworkInterfaceType==NetworkInterfaceType.Wireless80211 ||
                n.NetworkInterfaceType.ToString().StartsWith("Wwan",StringComparison.OrdinalIgnoreCase))) return "a Wi-Fi or WWAN adapter is present";
            var radios=SystemRadios();
            if(radios.Length!=1 || RadioProperty(radios[0],"Kind")!="Bluetooth") return "radio inventory is not exactly one Bluetooth adapter";
            var state=RadioProperty(radios[0],"State");
            if(state!="On" && state!="Off") return "Bluetooth is hardware-disabled or its state is unknown";
            if(ConnectedBluetoothCount(false)!=0 || ConnectedBluetoothCount(true)!=0) return "a Classic or LE Bluetooth device is connected";
            return null;
        } catch(Exception ex) { return "cannot verify radio/connection guard ("+ex.GetBaseException().GetType().Name+")"; }
    }
    static string QuickDeniedText()
    {
        using(var stream=typeof(UserSettings).Assembly.GetManifestResourceStream("Lunaris.Languages."+SettingsStore.Load().Language+".txt"))
        using(var reader=new StreamReader(stream)) {
            string line; while((line=reader.ReadLine())!=null) if(line.StartsWith("QuickDenied=")) return line.Substring(12);
            throw new Exception("Missing radio denial message resource.");
        }
    }
    static bool TryRadioClick(AE menu,string name,bool expected,Func<bool> systemState)
    {
        string guard=Task.Run((Func<string>)RadioGuard).GetAwaiter().GetResult();
        if(guard!=null) { Console.WriteLine("SKIP "+name+" switch: "+guard); return false; }
        var toggle=Control(menu,"StatusMenu."+name);
        if(!Visible(toggle) || !toggle.Current.IsEnabled) { Console.WriteLine("SKIP "+name+" switch: unavailable according to Windows"); return false; }
        if(systemState()==expected || RadioControl(toggle)!=(expected ? "off" : "on"))
            throw new Exception(name+" initial state changed before click");
        Move(Center(toggle,.035)); mouse_event(2,0,0,0,UIntPtr.Zero); mouse_event(4,0,0,0,UIntPtr.Zero);
        string denied=QuickDeniedText();
        var timer=Stopwatch.StartNew();
        do {
            if(systemState()==expected) {
                Until(delegate {
                    var row=Control(menu,"StatusMenu."+name);
                    if(row==null || row.Current.ItemStatus!=(expected ? "on" : "off")) return false;
                    var toggleState=((TogglePattern)row.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState;
                    return toggleState==(expected ? ToggleState.On : ToggleState.Off);
                },
                    name+" menu click changed the actual system state and the checkbox");
                return true;
            }
            if(Visible(Find(menu,AE.NameProperty,denied))) {
                Console.WriteLine("SKIP "+name+" switch: Windows rejected the menu request; no system change verified");
                return false;
            }
            Thread.Sleep(150);
        } while(timer.ElapsedMilliseconds<12500);
        throw new Exception(name+" click never reached the requested system state.");
    }
    static void RestoreBluetooth(bool initial)
    {
        // Airplane restoration is asynchronous and may restore Bluetooth itself.
        var stateType=RadioType.GetMethod("SetStateAsync").GetParameters()[0].ParameterType;
        var desired=Enum.Parse(stateType,initial ? "On" : "Off");
        var stable=Stopwatch.StartNew(); var time=Stopwatch.StartNew(); long nextRestore=0;
        do {
            var current=Task.Run((Func<string[]>)BluetoothStates).GetAwaiter().GetResult();
            if(current.Length==1 && current[0]==(initial ? "on" : "off")) {
                if(stable.ElapsedMilliseconds>=1200) { Console.WriteLine("PASS original Bluetooth state is stable"); return; }
            } else {
                stable.Restart();
                if(current.Length==1 && current[0]!="unavailable" && time.ElapsedMilliseconds>=nextRestore) {
                    var result=Task.Run(()=> {
                        var bt=SystemRadios().Single(r=>RadioProperty(r,"Kind")=="Bluetooth");
                        return WinRtResult(RadioType.GetMethod("SetStateAsync"),bt,desired).ToString();
                    }).GetAwaiter().GetResult();
                    if(result!="Allowed") throw new Exception("Independent Bluetooth restoration was denied: "+result);
                    nextRestore=time.ElapsedMilliseconds+1000;
                }
            }
            Thread.Sleep(150);
        } while(time.ElapsedMilliseconds<8000);
        throw new Exception("Unable to restore the original Bluetooth state.");
    }
    static void ExerciseRadioMenu(AE menu)
    {
        string guard=Task.Run((Func<string>)RadioGuard).GetAwaiter().GetResult();
        if(guard!=null) { Console.WriteLine("SKIP active Bluetooth and airplane tests: "+guard); return; }
        string access;
        try { access=WinRtResult(RadioType.GetMethod("RequestAccessAsync"),null).ToString(); }
        catch(Exception ex) { Console.WriteLine("SKIP radio switches: independent Bluetooth recovery access unavailable ("+ex.GetBaseException().GetType().Name+")"); return; }
        if(access!="Allowed") { Console.WriteLine("SKIP radio switches: Bluetooth recovery access is "+access); return; }
        ITestFlight manager;
        try { manager=(ITestFlight)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("581333F6-28DB-41BE-BC7A-FF201F12F3F6"),true)); }
        catch(COMException) { Console.WriteLine("SKIP radio switches: cannot open independent system radio manager"); return; }
        try {
            int supported,enabled,ui,reason;
            manager.IsSupported(out supported); manager.GetState(out enabled,out ui,out reason);
            if(supported==0 || (enabled!=0 && enabled!=1)) { Console.WriteLine("SKIP radio switches: system airplane state unavailable"); return; }
            bool airplane=FlightState(), bt=BluetoothState();
            if(airplane!=(enabled==0)) throw new Exception("System airplane observations disagree before the test.");
            try {
                // Restore BT before the airplane gesture so Windows captures the
                // user's original per-radio state on entering airplane mode.
                if(!TryRadioClick(menu,"Bluetooth",!bt,BluetoothState)) {
                    Console.WriteLine("SKIP airplane switch: Bluetooth switching could not be confirmed");
                    return;
                }
                RestoreBluetooth(bt);
                Until(()=>RadioControl(Control(menu,"StatusMenu.Bluetooth"))==(bt ? "on" : "off"),"Bluetooth menu shows restored state");
                TryRadioClick(menu,"Airplane",!airplane,FlightState);
            } finally {
                try {
                    if(FlightState()!=airplane) manager.SetState(airplane ? 0 : 1);
                    Until(delegate { manager.GetState(out enabled,out ui,out reason);
                        return FlightState()==airplane && enabled==(airplane ? 0 : 1); },"original global airplane state restored");
                } finally { RestoreBluetooth(bt); }
            }
        } finally { Marshal.ReleaseComObject(manager); }
    }
    static void CheckRadioDisplay(AE bar,bool exercise)
    {
        var bt=Task.Run((Func<string[]>)BluetoothStates).GetAwaiter().GetResult();
        object flight=Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\RadioManagement\SystemRadioState","",null);
        var menu=OpenMenu(bar,"Network");
        try {
            var air=WaitFor(()=>Control(menu,"StatusMenu.Airplane"),"airplane mode is in the network menu");
            Until(()=> {
                var bluetooth=Buttons(menu,"StatusMenu.Bluetooth");
                return bluetooth.Length>0 && bluetooth.Select(RadioControl).OrderBy(s=>s)
                    .SequenceEqual(bt.Length==0 ? new[]{"unavailable"} : bt);
            },"Bluetooth checkboxes agree with the physical radio inventory");
            string state=RadioControl(air);
            if(flight is int && state!="unavailable" && state!=((int)flight==0 ? "off" : "on"))
                throw new Exception("Airplane display disagrees with Windows SystemRadioState.");
            Console.WriteLine("PASS airplane mode visible ("+state+")");
            if(exercise) ExerciseRadioMenu(menu);
        } finally { EscapeMenu("Network"); }
    }
    static void DevicePreview(bool exerciseAudio,bool exerciseRadios)
    {
        var cursor=Cursor.Position; var foreground=GetForegroundWindow();
        try {
            ConfigureHoverFixture(); Start();
            foreach(var screen in Screen.AllScreens) CheckHoverCorners(screen);
            var bar=WaitFor(()=>Window("StatusBar"),"preview status bar is ready");
            CheckRadioDisplay(bar,exerciseRadios);
            CheckAudioDevices(bar,exerciseAudio);
        } finally {
            Stop(); SetCursorPos(cursor.X,cursor.Y); if(foreground!=IntPtr.Zero) SetForegroundWindow(foreground);
        }
    }

    [ComImport,Guid("F8679F50-850A-41CF-9C72-430F290290C8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITestAudioPolicy {
        void GetMixFormat(); void GetFormat(); void ResetFormat(); void SetFormat(); void GetPeriod(); void SetPeriod();
        void GetShareMode(); void SetShareMode(); void GetProperty(); void SetProperty();
        void SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)]string id,int role);
    }
    static string DeviceId(ITestDevice device)
    {
        try { string id; device.GetId(out id); return id; }
        finally { Marshal.ReleaseComObject(device); }
    }
    static string[] DefaultRoles(ITestDevices devices,int flow)
    {
        var ids=new string[3];
        for(int role=0;role<3;role++) {
            try { ITestDevice d; devices.GetDefaultAudioEndpoint(flow,role,out d); ids[role]=DeviceId(d); }
            catch(COMException e) { if(e.ErrorCode!=unchecked((int)0x80070490)) throw; }
        }
        return ids;
    }
    static string[] AvailableAudio(ITestDevices devices,int flow)
    {
        ITestCollection list; devices.EnumAudioEndpoints(flow,1,out list);
        try {
            uint count; list.GetCount(out count); var ids=new List<string>();
            for(uint i=0;i<count;i++) { ITestDevice d; list.Item(i,out d); ids.Add(DeviceId(d)); }
            return ids.ToArray();
        } finally { Marshal.ReleaseComObject(list); }
    }
    static void RestoreRoles(ITestDevices devices,int flow,string[] original)
    {
        var now=DefaultRoles(devices,flow);
        if(original.SequenceEqual(now)) return;
        var policy=(ITestAudioPolicy)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9"),true));
        try { for(int i=0;i<3;i++) if(now[i]!=original[i]) policy.SetDefaultEndpoint(original[i],i); }
        finally { Marshal.ReleaseComObject(policy); }
        Until(()=>DefaultRoles(devices,flow).SequenceEqual(original),"restored all original default endpoint roles");
    }
    static void CheckAudioLevels(ITestDevices devices,AE menu,int flow,string id,bool exercise)
    {
        string prefix="StatusMenu."+(flow==1 ? "Input" : "");
        ITestDevice d; devices.GetDevice(id,out d);
        object endpoint=null;
        try {
            var iid=typeof(ITestVolume).GUID; d.Activate(ref iid,1,IntPtr.Zero,out endpoint);
            var audio=(ITestVolume)endpoint;
            float initial; bool muted; audio.GetScalar(out initial); audio.GetMuted(out muted);
            var slider=WaitFor(()=> { var e=Control(menu,prefix+"Volume"); return Visible(e) ? e : null; },"endpoint slider is visible");
            var range=(RangeValuePattern)slider.GetCurrentPattern(RangeValuePattern.Pattern);
            var toggle=Control(menu,prefix+"Mute");
            var check=(TogglePattern)toggle.GetCurrentPattern(TogglePattern.Pattern);
            if(Math.Abs(range.Current.Value-initial*100)>1 || (check.Current.ToggleState==ToggleState.On)!=muted)
                throw new Exception("The shown level or mute disagrees with its selected Core Audio endpoint.");
            if(!exercise) { Console.WriteLine("PASS level and mute agree with the active endpoint"); return; }
            try {
                var target=initial>=.1f ? initial-.06f : initial+.06f;
                range.SetValue(Math.Round(target*100));
                Until(delegate { float actual; audio.GetScalar(out actual); return Math.Abs(actual-target)<.02; },"slider changed the selected endpoint level");
                bool beforeClick; audio.GetMuted(out beforeClick);
                Until(()=> (check.Current.ToggleState==ToggleState.On)==beforeClick,
                    "mute reflects the endpoint after its level changed");
                // Some endpoint drivers update mute when their level changes.
                // Use the checkbox peer so a popup resize cannot redirect this action.
                check.Toggle();
                Until(delegate { bool after; audio.GetMuted(out after); return beforeClick!=after; },"mute changed the selected endpoint");
            } finally {
                Guid context=Guid.Empty;
                try { audio.SetScalar(initial,ref context); } finally { audio.SetMuted(muted,ref context); }
                Until(delegate { float a; bool m; audio.GetScalar(out a); audio.GetMuted(out m);
                    return Math.Abs(a-initial)<.00001 && m==muted; },"restored the original endpoint scalar and mute");
            }
        } finally { if(endpoint!=null) Marshal.ReleaseComObject(endpoint); Marshal.ReleaseComObject(d); }
    }
    static void DeviceDirection(ITestDevices devices,AE menu,int flow,bool exercise)
    {
        string prefix="StatusMenu."+(flow==1 ? "Input" : "Output");
        string[] original=DefaultRoles(devices,flow), opposite=DefaultRoles(devices,1-flow), inventory=AvailableAudio(devices,flow);
        if(inventory.Length==0) {
            Until(delegate { var line=Control(menu,prefix+"State"); return Visible(line) && !String.IsNullOrWhiteSpace(line.Current.Name) &&
                !Visible(Control(menu,prefix+"Device")); },"menu explains that no active endpoint is available");
            return;
        }
        var chooser=WaitFor(()=> { var e=Control(menu,prefix+"Device"); return Visible(e) && e.Current.ItemStatus==(original[1]??"") ? e:null; },
            "chooser identifies the Windows multimedia default");
        Click(chooser,false);
        var options=Buttons(menu,prefix+"Option");
        if(!options.Select(e=>e.Current.ItemStatus).OrderBy(s=>s).SequenceEqual(inventory.OrderBy(s=>s)) ||
            !options.Where(e=>e.Current.HelpText=="current").Select(e=>e.Current.ItemStatus).SequenceEqual(original.Skip(1).Take(1).Where(s=>s!=null)) ||
            options.Any(e=>String.IsNullOrWhiteSpace(e.Current.Name))) throw new Exception("Endpoint choices differ from the Windows active endpoint list.");
        var next=exercise && original.All(s=>s!=null) ? inventory.FirstOrDefault(s=>s!=original[1]) : null;
        try {
            if(next!=null) {
                var option=options.Single(e=>e.Current.ItemStatus==next);
                ((InvokePattern)option.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                Until(delegate { var role=DefaultRoles(devices,flow);
                    return role[0]==next && role[1]==next && role[2]==original[2] && chooser.Current.ItemStatus==next;
                },"device selection updates console and multimedia roles while preserving communications");
            } else {
                Click(chooser,false);
                Console.WriteLine(exercise ? "SKIP alternative device selection: no restorable alternative" : "PASS read-only chooser lists the active endpoints");
            }
            if((next??original[1])!=null) CheckAudioLevels(devices,menu,flow,next??original[1],exercise);
            if(!DefaultRoles(devices,1-flow).SequenceEqual(opposite)) throw new Exception("Selection changed the opposite audio direction.");
        } finally {
            if(exercise) {
                try { RestoreRoles(devices,flow,original); }
                finally { RestoreRoles(devices,1-flow,opposite); }
            }
        }
    }
    static void CheckAudioDevices(AE bar,bool exercise)
    {
        var devices=(ITestDevices)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"),true));
        try {
            var menu=OpenMenu(bar,"Sound");
            try { DeviceDirection(devices,menu,0,exercise); DeviceDirection(devices,menu,1,exercise); }
            finally { EscapeMenu("Sound"); }
        } finally { Marshal.ReleaseComObject(devices); }
    }
}
