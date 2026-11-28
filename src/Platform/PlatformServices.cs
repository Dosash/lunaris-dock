using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Lunaris
{
    public sealed class PlatformServices : IDisposable
    {
        private readonly bool preview;
        internal bool Preview { get { return preview; } }
        private readonly ShellCatalog catalog = new ShellCatalog();
        private readonly WindowList windows;
        private readonly TaskbarGuard guard = new TaskbarGuard();
        private readonly Dispatcher dispatcher;
        private readonly List<FileSystemWatcher> folders = new List<FileSystemWatcher>();
        private WinKey winKey;
        private readonly WindowsStartRedirect windowsStart;
        private HwndSource receiver;
        private DispatcherTimer refresh, heartbeat;
        private uint taskbarCreated, registration;
        private IntPtr appsFolder;
        private bool replace, disposed;
        private const int AppsChanged = 0x8054;
        public event Action ShellChanged;
        public event Action ToggleRequested;
        internal event Action WindowsStartRequested;
        public event Action<string> IntegrationError;

        public PlatformServices(bool preview)
        {
            this.preview = preview;
            dispatcher = Dispatcher.CurrentDispatcher;
            windows = new WindowList(catalog);
            windowsStart = new WindowsStartRedirect(dispatcher, delegate {
                var handler = WindowsStartRequested;
                if (!disposed && handler != null) handler();
            });
        }

        public void Start()
        {
            var parameters = new HwndSourceParameters("Lunaris Shell Notifications");
            parameters.WindowStyle = unchecked((int)0x80000000); // Hidden top-level window receives TaskbarCreated.
            parameters.Width = parameters.Height = 0;
            receiver = new HwndSource(parameters);
            receiver.AddHook(OnShellMessage);
            taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(850) };
            refresh.Tick += delegate
            {
                refresh.Stop();
                var handler = ShellChanged;
                if (handler != null) handler();
            };
            uint attrs;
            if (SHParseDisplayName("shell:AppsFolder", IntPtr.Zero, out appsFolder, 0, out attrs) == 0)
            {
                var entry = new NotifyEntry { Item = appsFolder, Recursive = true };
                registration = SHChangeNotifyRegister(receiver.Handle, 0x8003, 0x0002301F | 0x08000000,
                    AppsChanged, 1, ref entry);
            }
            Watch(Environment.GetFolderPath(Environment.SpecialFolder.Programs));
            Watch(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms));
            if (preview) return;
            TaskbarGuard.Recover();
            winKey = new WinKey(delegate
            {
                dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(delegate
                {
                    var handler = ToggleRequested;
                    if (!disposed && handler != null) handler();
                }));
            });
            winKey.Start();
            heartbeat = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            heartbeat.Tick += delegate
            {
                if (!replace || guard.Running) return;
                try { guard.Start(); }
                catch (Exception ex)
                {
                    replace = false;
                    var error = IntegrationError;
                    if (error != null) error(ex.Message);
                }
            };
            heartbeat.Start();
        }

        private void Watch(string path)
        {
            if (!Directory.Exists(path)) return;
            var watcher = new FileSystemWatcher(path);
            watcher.IncludeSubdirectories = true;
            watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite;
            watcher.Created += OnFile;
            watcher.Changed += OnFile;
            watcher.Deleted += OnFile;
            watcher.Renamed += delegate { ScheduleRefresh(); };
            watcher.Error += delegate { ScheduleRefresh(); };
            folders.Add(watcher);
            watcher.EnableRaisingEvents = true;
        }

        private void OnFile(object sender, FileSystemEventArgs args) { ScheduleRefresh(); }

        private IntPtr OnShellMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == AppsChanged)
            {
                IntPtr ids;
                int events;
                IntPtr lease = SHChangeNotification_Lock(wParam, unchecked((uint)lParam.ToInt64()), out ids, out events);
                if (lease != IntPtr.Zero)
                {
                    SHChangeNotification_Unlock(lease);
                    ScheduleRefresh();
                }
                handled = true;
            }
            else if (taskbarCreated != 0 && message == (int)taskbarCreated)
                ScheduleRefresh();
            return IntPtr.Zero;
        }

        private void ScheduleRefresh()
        {
            if (dispatcher.HasShutdownStarted) return;
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
            {
                if (disposed || refresh == null) return;
                refresh.Stop();
                refresh.Start();
            }));
        }

        public List<AppEntry> GetApplications() { return catalog.Read(); }
        public List<WindowEntry> GetWindows() { return windows.Read(); }
        public AppEntry ResolveApp(string id) { return catalog.Resolve(id); }
        internal AppEntry ResolveExecutablePin(string path) { return catalog.ResolveExecutablePin(path); }
        internal string PinIdentity(string pin) { return catalog.PinIdentity(pin); }
        internal string PinTarget(string id) { return catalog.PinTarget(id); }
        internal string[] ResolveDrop(string[] files) { return catalog.ResolveDrop(files); }
        public void Launch(AppEntry app) { catalog.Launch(app); }
        public void Activate(WindowEntry window) { if (window != null) WindowList.Focus(window.Handle); }
        public void FocusWindow(IntPtr hwnd) { WindowList.Focus(hwnd); }
        public bool IsFullscreen() { return windows.IsFullscreen(); }
        internal IntPtr FullscreenMonitor() { return windows.FullscreenMonitor(false); }
        internal HashSet<string> CoveredScreens(ScreenInfo[] screens) { return windows.CoveredScreens(screens); }

        internal void ApplyWindowsStart(bool redirect)
        {
            if (preview || disposed) return;
            if (redirect) windowsStart.Enable(); else windowsStart.Dispose();
        }

        public void ApplyTaskbar(bool enabled)
        {
            if (preview || disposed) return;
            if (enabled)
            {
                guard.Start();
                replace = true;
            }
            else
            {
                replace = false;
                guard.Stop();
            }
        }

        public void ApplyStartup(bool enabled)
        {
            if (preview || disposed) return;
            using (var key = enabled ? Registry.CurrentUser.CreateSubKey(RunKey) : Registry.CurrentUser.OpenSubKey(RunKey, true))
            using (var self = Process.GetCurrentProcess())
            {
                if (key == null) return;
                string path = self.MainModule.FileName;
                if (enabled)
                    key.SetValue(StartupValue, "\"" + path + "\"", RegistryValueKind.String);
                else if (OwnsStartupValue(key, path))
                    key.DeleteValue(StartupValue, false);
            }
        }

        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private static string StartupValue { get { return SettingsStore.Portable ? "LunarisDockPortable" : "LunarisDock"; } }

        internal bool IsStartupRegistered()
        {
            if (preview) return false;
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey, false))
            using (var self = Process.GetCurrentProcess())
                return key != null && OwnsStartupValue(key, self.MainModule.FileName);
        }

        private static bool OwnsStartupValue(RegistryKey key, string path)
        {
            string value = key.GetValue(StartupValue, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            return String.Equals(value, "\"" + path + "\"", StringComparison.OrdinalIgnoreCase) ||
                   String.Equals(value, path, StringComparison.OrdinalIgnoreCase);
        }

        public static int RunGuardian(string[] args) { return TaskbarGuard.Run(args); }
        public static void RecoverTaskbar() { TaskbarGuard.Recover(); }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            replace = false;
            if (heartbeat != null) heartbeat.Stop();
            if (winKey != null) winKey.Dispose();
            windowsStart.Dispose();
            if (refresh != null) refresh.Stop();
            foreach (var watcher in folders) watcher.Dispose();
            if (registration != 0) SHChangeNotifyDeregister(registration);
            if (appsFolder != IntPtr.Zero) Marshal.FreeCoTaskMem(appsFolder);
            if (receiver != null) receiver.Dispose();
            guard.Dispose();
        }

        [StructLayout(LayoutKind.Sequential)] private struct NotifyEntry { public IntPtr Item; [MarshalAs(UnmanagedType.Bool)] public bool Recursive; }
        [DllImport("shell32.dll", CharSet=CharSet.Unicode)] private static extern int SHParseDisplayName(string name, IntPtr ctx, out IntPtr item, uint attrsIn, out uint attrs);
        [DllImport("shell32.dll")] private static extern uint SHChangeNotifyRegister(IntPtr window, int sources, int events, uint message, int count, ref NotifyEntry entry);
        [DllImport("shell32.dll")] private static extern bool SHChangeNotifyDeregister(uint id);
        [DllImport("shell32.dll")] private static extern IntPtr SHChangeNotification_Lock(IntPtr data, uint process, out IntPtr ids, out int events);
        [DllImport("shell32.dll")] private static extern bool SHChangeNotification_Unlock(IntPtr data);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    }
}
