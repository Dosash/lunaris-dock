using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static partial class PreviewTests
{
    // A separate, ordinary WinForms application. The driver addresses only its
    // tagged top-level HWND. NotifyIcon and the context menu use their normal paths.
    enum Probe { Left=1, Right, Command, ChangeArt, MenuX, MenuY, InputLength, AnchorX, AnchorY, RectLeft, RectTop, RectRight, RectBottom, RectResult }
    const uint FixtureRequest = 0x8000 + 315;
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int RegisterWindowMessage(string name);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    struct NotifyData {
        public int Size; public IntPtr Owner; public uint Id, Flags, Message; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public IntPtr Balloon;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct IconIdentifier { public int Size; public IntPtr Owner; public uint Id; public Guid Guid; }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] static extern bool Shell_NotifyIcon(uint command,ref NotifyData icon);
    [DllImport("shell32.dll")] static extern int Shell_NotifyIconGetRect(ref IconIdentifier id,out NativeRect bounds);

    sealed class StatusFixtureForm : Form
    {
        readonly Icon first, second;
        readonly NotifyIcon tray;
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly TextBox input = new TextBox { Dock=DockStyle.Fill, Multiline=true };
        readonly bool versionFour;
        readonly Guid iconGuid=Guid.NewGuid();
        const uint NativeId=0xA0B1, Callback=0x8000+825;
        readonly int taskbarCreated=RegisterWindowMessage("TaskbarCreated");
        Point anchor;
        NativeRect reported;
        int rectangleResult=-1;
        bool alternate;
        int left, right, commands;

        static Icon MakeIcon(bool alternate)
        {
            using(var bitmap=new Bitmap(32,32,PixelFormat.Format32bppArgb))
            using(var g=Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                g.FillRectangle(alternate ? Brushes.Cyan : Brushes.Magenta,3,3,10,8);
                g.FillRectangle(alternate ? Brushes.Magenta : Brushes.Cyan,19,19,10,10);
                IntPtr handle=bitmap.GetHicon();
                try { using(var borrowed=Icon.FromHandle(handle)) return (Icon)borrowed.Clone(); }
                finally { DestroyIcon(handle); }
            }
        }
        internal StatusFixtureForm(string tag,bool native)
        {
            versionFour=native;
            Text=tag; ShowInTaskbar=false;
            StartPosition=FormStartPosition.Manual;
            var r=Screen.PrimaryScreen.WorkingArea;
            Bounds=new Rectangle(r.Left+r.Width/4,r.Top+r.Height/3,275,135);
            Controls.Add(input);
            first=MakeIcon(false); second=MakeIcon(true);
            menu.Items.Add("Lunaris fixture command",null,delegate { commands++; });
            if(!versionFour) {
                tray=new NotifyIcon { Text=tag, Icon=first, ContextMenuStrip=menu };
                tray.MouseClick+=delegate(object s, MouseEventArgs e) {
                    if(e.Button==MouseButtons.Left) left++;
                    if(e.Button==MouseButtons.Right) right++;
                };
            }
            Shown+=delegate {
                input.Focus();
                if(versionFour) RegisterNative(); else tray.Visible=true;
            };
        }
        NotifyData Data()
        {
            return new NotifyData { Size=Marshal.SizeOf(typeof(NotifyData)), Owner=Handle, Id=NativeId, Guid=iconGuid,
                Message=Callback, Flags=0x20|7, Tip=Text, Icon=(alternate ? second : first).Handle };
        }
        void RegisterNative()
        {
            var data=Data(); Shell_NotifyIcon(0,ref data);
            data.Version=4;
            Shell_NotifyIcon(4,ref data);
        }
        void NativeAction(ref Message m)
        {
            uint packed=unchecked((uint)m.LParam.ToInt64());
            if(packed>>16!=NativeId) return;
            uint action=packed & 0xffff;
            if(action!=0x0400 && action!=0x007b) return;
            long pos=m.WParam.ToInt64();
            anchor=new Point(unchecked((short)pos),unchecked((short)(pos>>16)));
            var id=new IconIdentifier { Size=Marshal.SizeOf(typeof(IconIdentifier)), Owner=Handle, Id=NativeId, Guid=iconGuid };
            rectangleResult=Shell_NotifyIconGetRect(ref id,out reported);
            if(action==0x0400) left++;
            else { right++; SetForegroundWindow(Handle); menu.Show(anchor); }
        }
        protected override void WndProc(ref Message m)
        {
            if(versionFour && m.Msg==taskbarCreated && Visible && !Disposing) RegisterNative();
            if(versionFour && m.Msg==Callback) { NativeAction(ref m); return; }
            if(m.Msg==FixtureRequest) {
                int value=0;
                switch((Probe)m.WParam.ToInt32()) {
                    case Probe.Left: value=left; break;
                    case Probe.Right: value=right; break;
                    case Probe.Command: value=commands; break;
                    case Probe.InputLength: value=input.TextLength; break;
                    case Probe.ChangeArt:
                        alternate=m.LParam!=IntPtr.Zero;
                        if(versionFour) { var data=Data(); value=Shell_NotifyIcon(1,ref data) ? 1 : 0; }
                        else { tray.Icon=alternate ? second : first; value=1; }
                        break;
                    case Probe.AnchorX: value=anchor.X; break;
                    case Probe.AnchorY: value=anchor.Y; break;
                    case Probe.RectResult: value=rectangleResult; break;
                    case Probe.RectLeft: value=reported.Left; break;
                    case Probe.RectTop: value=reported.Top; break;
                    case Probe.RectRight: value=reported.Right; break;
                    case Probe.RectBottom: value=reported.Bottom; break;
                    case Probe.MenuX: case Probe.MenuY:
                        if(menu.Visible) {
                            var b=menu.Items[0].Bounds;
                            var p=menu.PointToScreen(new Point(b.Left+b.Width/2,b.Top+b.Height/2));
                            value=(Probe)m.WParam.ToInt32()==Probe.MenuX ? p.X : p.Y;
                        } else value=Int32.MinValue;
                        break;
                }
                m.Result=new IntPtr(value); return;
            }
            base.WndProc(ref m);
        }
        protected override void Dispose(bool disposing)
        {
            if(disposing && !IsDisposed) {
                if(versionFour && IsHandleCreated) { var data=Data(); Shell_NotifyIcon(2,ref data); }
                if(tray!=null) { tray.Visible=false; tray.Dispose(); }
                menu.Dispose();
                first.Dispose(); second.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal static bool RunStatusFixture(string[] args)
    {
        if(args.Length!=2 || (args[0]!="--status-fixture" && args[0]!="--status-fixture-v4")) return false;
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        using(var form=new StatusFixtureForm(args[1],args[0]=="--status-fixture-v4")) Application.Run(form);
        return true;
    }

    sealed class StatusFixture : IDisposable
    {
        internal readonly string Label="Lunaris-QA-"+Guid.NewGuid().ToString("N").Substring(0,12);
        internal readonly Process Process;
        internal readonly IntPtr Hwnd;

        internal StatusFixture(bool versionFour=false)
        {
            using(var me=Process.GetCurrentProcess())
                Process=Process.Start(new ProcessStartInfo(me.MainModule.FileName,(versionFour ? "--status-fixture-v4 " : "--status-fixture ")+Label) {
                    UseShellExecute=false, CreateNoWindow=true
                });
            try {
                Until(()=>FindWindow(null,Label)!=IntPtr.Zero && Owner(FindWindow(null,Label))==Process.Id,
                    "separate fixture application is ready");
                Hwnd=FindWindow(null,Label);
            } catch { Dispose(); throw; }
        }
        internal int Read(Probe key,int value=0)
        {
            IntPtr result;
            if(Owner(Hwnd)!=Process.Id ||
                SendMessageTimeout(Hwnd,FixtureRequest,new IntPtr((int)key),new IntPtr(value),2,1500,out result)==IntPtr.Zero)
                throw new Exception("Fixture did not respond: "+key);
            return result.ToInt32();
        }
        internal void ClickOwned(Point p)
        {
            SetCursorPos(p.X,p.Y);
            if(Cursor.Position!=p || Owner(WindowFromPoint(p))!=Process.Id) throw new Exception("Fixture click is covered.");
            mouse_event(2,0,0,0,UIntPtr.Zero); mouse_event(4,0,0,0,UIntPtr.Zero);
        }
        internal void FocusEditor(bool raise=true)
        {
            NativeRect box;
            if(!GetWindowRect(Hwnd,out box)) throw new Exception("Fixture is missing.");
            if(raise) SetForegroundWindow(Hwnd);
            ClickOwned(new Point((box.Left+box.Right)/2,(box.Top+box.Bottom)/2));
            Until(()=>GetForegroundWindow()==Hwnd,"fixture editor has foreground focus");
        }
        public void Dispose()
        {
            if(!Process.HasExited) {
                if(Owner(Hwnd)==Process.Id) PostMessage(Hwnd,0x10,IntPtr.Zero,IntPtr.Zero);
                if(!Process.WaitForExit(2000)) { Process.Kill(); Process.WaitForExit(); }
            }
            Process.Dispose();
        }
    }
}
