// Captures the running Release executable on a new Win32 desktop in the same session.
// No Explorer instance, registry changes, global shortcuts, or taskbar replacement.
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;
using AE = System.Windows.Automation.AutomationElement;

internal static class CaptureDesktop
{
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    struct Startup {
        public int size;
        public string reserved, desktop, title;
        public int x, y, w, h, columns, rows, fill, flags;
        public short show, reservedSize;
        public IntPtr reservedPtr, input, output, error;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct Child { public IntPtr process, thread; public int pid, tid; }
    [DllImport("user32", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, int flags, uint access, IntPtr security);
    [DllImport("user32", SetLastError=true)] static extern IntPtr OpenInputDesktop(int flags, bool inherit, uint access);
    [DllImport("user32", SetLastError=true)] static extern bool SwitchDesktop(IntPtr desktop);
    [DllImport("user32")] static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern bool CreateProcess(string exe, StringBuilder command, IntPtr p, IntPtr t, bool inherit,
        uint flags, IntPtr env, string cwd, ref Startup startup, out Child child);
    [DllImport("kernel32")] static extern bool CloseHandle(IntPtr value);
    [DllImport("kernel32")] static extern uint WaitForSingleObject(IntPtr h, uint milliseconds);
    [DllImport("kernel32")] static extern bool GetExitCodeProcess(IntPtr h, out int code);
    [DllImport("user32")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32", CharSet=CharSet.Unicode)] static extern bool SystemParametersInfo(int action, int size, StringBuilder value, int flags);
    [DllImport("user32")] static extern bool PaintDesktop(IntPtr dc);
    [DllImport("user32", CharSet=CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);
    [DllImport("user32")] static extern IntPtr GetWindow(IntPtr handle, int command);
    [DllImport("user32")] static extern uint GetWindowThreadProcessId(IntPtr handle, out int processId);
    [DllImport("user32")] static extern bool PostMessage(IntPtr handle, uint message, IntPtr w, IntPtr l);
    [DllImport("user32")] static extern bool IsWindow(IntPtr handle);

    // On an alternate desktop Explorer does not create a wallpaper window. Ask USER32
    // for the current wallpaper. Paint only this test window, behind the actual Dock.
    private sealed class Wallpaper : Form
    {
        public Wallpaper() {
            FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
            Bounds = Screen.PrimaryScreen.Bounds; ShowInTaskbar = false;
            var path = new StringBuilder(32768);
            if (SystemParametersInfo(0x73, path.Capacity, path, 0) && File.Exists(path.ToString()))
                using (var source = Image.FromFile(path.ToString())) BackgroundImage = new Bitmap(source);
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x08000080; return cp; }
        }
        protected override void OnPaintBackground(PaintEventArgs e) {
            if (BackgroundImage != null) {
                double scale = Math.Max((double)Width / BackgroundImage.Width, (double)Height / BackgroundImage.Height);
                int w = (int)(BackgroundImage.Width * scale), h = (int)(BackgroundImage.Height * scale);
                e.Graphics.DrawImage(BackgroundImage, (Width-w)/2, (Height-h)/2, w, h);
                return;
            }
            IntPtr dc = e.Graphics.GetHdc();
            try { PaintDesktop(dc); } finally { e.Graphics.ReleaseHdc(dc); }
        }
    }

    static string Quote(string value) { return "\"" + value + "\""; }
    static int Isolated(string exe, string folder, bool runTests)
    {
        var cursor = Cursor.Position;
        var name = "LunarisQA" + Guid.NewGuid().ToString("N");
        IntPtr original = OpenInputDesktop(0, false, 0x100);
        if (original == IntPtr.Zero) throw new Win32Exception();
        IntPtr desktop = CreateDesktop(name, IntPtr.Zero, IntPtr.Zero, 0, 0x000F01FF, IntPtr.Zero);
        try {
            if (desktop == IntPtr.Zero || !SwitchDesktop(desktop)) throw new Win32Exception();
            string self = Process.GetCurrentProcess().MainModule.FileName;
            var start = new Startup { size = Marshal.SizeOf(typeof(Startup)), desktop = "WinSta0\\" + name };
            string program = runTests ? Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe") : self;
            string args = runTests ? " -NoProfile -ExecutionPolicy RemoteSigned -File " + Quote(exe) + " -PrivateDesktop -OutDir " + Quote(folder) :
                " /capture " + Quote(exe) + " " + Quote(folder);
            var cmd = new StringBuilder(Quote(program) + args);
            Child child;
            if (!CreateProcess(program, cmd, IntPtr.Zero, IntPtr.Zero, false, 0x08000000, IntPtr.Zero,
                    Environment.CurrentDirectory, ref start, out child)) throw new Win32Exception();
            try {
                if (WaitForSingleObject(child.process, runTests ? 420000u : 120000u) != 0)
                    throw new TimeoutException("Истекло время ожидания тестового рабочего стола.");
                int code;
                if (!GetExitCodeProcess(child.process, out code)) throw new Win32Exception();
                return code;
            } finally { CloseHandle(child.thread); CloseHandle(child.process); }
        } finally {
            SwitchDesktop(original);
            Cursor.Position = cursor;
            if (desktop != IntPtr.Zero) CloseDesktop(desktop);
            CloseDesktop(original);
        }
    }

    static AE WaitFor(int pid, string id, bool multiple, string alternateId = null)
    {
        Condition query=new PropertyCondition(AE.AutomationIdProperty,id);
        if(alternateId!=null) query=new OrCondition(query,new PropertyCondition(AE.AutomationIdProperty,alternateId));
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 40000) {
            Application.DoEvents();
            var windows = AE.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AE.ProcessIdProperty, pid));
            foreach (AE w in windows) {
                if (w.Current.IsOffscreen) continue;
                if (!multiple && w.Current.AutomationId == id) return w;
                var items = w.FindAll(TreeScope.Descendants,query);
                if (items.Count >= (multiple ? 3 : 1)) return items[0];
            }
            Thread.Sleep(100);
        }
        throw new TimeoutException("Элемент не появился: " + id);
    }

    static void Snapshot(string name)
    {
        var screen = Screen.PrimaryScreen.Bounds;
        Snapshot(name, screen);
    }

    static void Snapshot(string name, Rectangle screen)
    {
        using (var image = new Bitmap(screen.Width, screen.Height))
        using (var graphics = Graphics.FromImage(image)) {
            graphics.CopyFromScreen(screen.Location, Point.Empty, screen.Size, CopyPixelOperation.SourceCopy);
            image.Save(name, ImageFormat.Png);
        }
    }

    static void CapturePanel(string name, AE element, int bottom)
    {
        var r = element.Current.BoundingRectangle;
        var area = Rectangle.FromLTRB((int)Math.Floor(r.Left) - 35, (int)Math.Floor(r.Top) - 28,
            (int)Math.Ceiling(r.Right) + 35, (int)Math.Ceiling(r.Bottom) + bottom);
        area.Intersect(Screen.PrimaryScreen.Bounds);
        Snapshot(name, area);
    }

    static void Invoke(int pid, string id)
    {
        ((InvokePattern)WaitFor(pid, id, false).GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    }

    static void CheckColourChooser(int pid, AE settings, AE button)
    {
        var owner = new IntPtr(settings.Current.NativeWindowHandle);
        ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 5000) {
            IntPtr dialog = IntPtr.Zero;
            while ((dialog = FindWindowEx(IntPtr.Zero,dialog,"#32770",null)) != IntPtr.Zero) {
                int process;
                GetWindowThreadProcessId(dialog,out process);
                if (process != pid || GetWindow(dialog,4) != owner) continue;
                PostMessage(dialog,0x10,IntPtr.Zero,IntPtr.Zero);
                while (IsWindow(dialog) && watch.ElapsedMilliseconds < 7000) Thread.Sleep(40);
                if (IsWindow(dialog)) throw new Exception("The colour chooser did not dismiss.");
                return;
            }
            Thread.Sleep(50);
        }
        throw new Exception("The swatch did not open the settings window's colour chooser.");
    }

    static void Capture(string exe, string folder)
    {
        SetThreadDpiAwarenessContext(new IntPtr(-4));
        string profile = Path.Combine(Path.GetTempPath(), "LunarisCapture-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("LUNARIS_PROFILE", profile);
        // Preview skips shell calls. Exercise the same Dock placement used in replacement mode.
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "settings.json"), "{\"Theme\":\"light\",\"ReplaceTaskbar\":true,\"InitializePins\":true}");
        Cursor.Position = new Point(12, 12);
        using (var wallpaper = new Wallpaper()) {
            try {
                wallpaper.Show(); wallpaper.Refresh(); Application.DoEvents();
                using (var dock = Process.Start(new ProcessStartInfo(exe, "--preview") { UseShellExecute = false })) {
                    try {
                        WaitFor(dock.Id, "Dock.App", true);
                        var open = WaitFor(dock.Id, "OpenLaunchpad", false);
                        Thread.Sleep(900);
                        Snapshot(Path.Combine(folder, "Dock.png"));
                        ((InvokePattern)open.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                        WaitFor(dock.Id, "Launchpad.App", true, "Launchpad.Folder");
                        Thread.Sleep(1100);
                        Snapshot(Path.Combine(folder, "Launchpad.png"));
                        CapturePanel(Path.Combine(folder, "Start-light.png"), WaitFor(dock.Id,"Launchpad",false), 95);
                        Invoke(dock.Id,"Launchpad.More");
                        Invoke(dock.Id,"Launchpad.Action.DockSettings");
                        var prefs = WaitFor(dock.Id,"Settings",false);
                        Thread.Sleep(280);
                        CapturePanel(Path.Combine(folder,"Settings-light.png"),prefs,22);
                        var radio = WaitFor(dock.Id,"Theme.Dark",false);
                        ((SelectionItemPattern)radio.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                        var saved = Stopwatch.StartNew();
                        while (!File.ReadAllText(Path.Combine(profile,"settings.json")).Contains("\"Theme\":\"dark\"")) {
                            if (saved.ElapsedMilliseconds>6000) throw new Exception("Dark theme was not saved from open settings.");
                            Thread.Sleep(80);
                        }
                        Thread.Sleep(360);
                        CapturePanel(Path.Combine(folder,"Settings-dark.png"),prefs,22);
                        Invoke(dock.Id,"CloseSettings");
                        Thread.Sleep(450);
                        Invoke(dock.Id,"OpenLaunchpad");
                        WaitFor(dock.Id,"Launchpad.App",true,"Launchpad.Folder");
                        Thread.Sleep(450);
                        CapturePanel(Path.Combine(folder,"Start-dark.png"), WaitFor(dock.Id,"Launchpad",false),95);
                        Invoke(dock.Id,"Launchpad.More");
                        Invoke(dock.Id,"Launchpad.Action.DockSettings");
                        prefs = WaitFor(dock.Id,"Settings",false);
                        var custom = WaitFor(dock.Id,"Theme.Custom",false);
                        ((SelectionItemPattern)custom.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                        var sampleButton = WaitFor(dock.Id,"ChooseCustomAccent",false);
                        WaitFor(dock.Id,"CustomAccent",false);
                        Thread.Sleep(450);
                        CapturePanel(Path.Combine(folder,"Settings-custom.png"),prefs,22);
                        var swatch = sampleButton.Current.BoundingRectangle;
                        if (swatch.IsEmpty || sampleButton.Current.IsOffscreen || swatch.Width < 5) throw new Exception("The custom colour editor is not visible.");
                        using (var pixel = new Bitmap(1,1))
                        using (var graphics = Graphics.FromImage(pixel)) {
                            graphics.CopyFromScreen((int)(swatch.Left+swatch.Width/2),(int)(swatch.Top+swatch.Height/2),0,0,pixel.Size);
                            var rgb = pixel.GetPixel(0,0);
                            if (Math.Abs(rgb.R-186)+Math.Abs(rgb.G-167)+Math.Abs(rgb.B-255)>20)
                                throw new Exception("Custom accent swatch at " + swatch + " rendered as " + rgb + ".");
                        }
                        CheckColourChooser(dock.Id,prefs,sampleButton);
                        Invoke(dock.Id,"CloseSettings");
                        Thread.Sleep(350);
                        Invoke(dock.Id,"OpenLaunchpad");
                        WaitFor(dock.Id,"Launchpad.App",true,"Launchpad.Folder");
                        Thread.Sleep(400);
                        CapturePanel(Path.Combine(folder,"Start-custom.png"),WaitFor(dock.Id,"Launchpad",false),95);
                    } finally {
                        using (var close = Process.Start(new ProcessStartInfo(exe, "--exit") { UseShellExecute = false }))
                            close.WaitForExit(17000);
                        if (!dock.WaitForExit(2000)) dock.Kill();
                    }
                }
            } finally {
                File.Delete(Path.Combine(profile, "settings.json"));
                File.Delete(Path.Combine(profile, "settings.json.bak"));
                if (Directory.GetFileSystemEntries(profile).Length == 0) Directory.Delete(profile);
            }
        }
    }

    [STAThread] public static int Main(string[] args)
    {
        bool child = args[0] == "/capture", test = args[0] == "/tests";
        string exe = Path.GetFullPath(args[child || test ? 1 : 0]);
        string folder = Path.GetFullPath(args[child || test ? 2 : 1]);
        try {
            Directory.CreateDirectory(folder);
            if (!child) return Isolated(exe, folder, test);
            Capture(exe, folder); return 0;
        } catch(Exception ex) {
            File.WriteAllText(Path.Combine(folder, "capture-error.txt"), ex.ToString());
            return 1;
        }
    }
}
