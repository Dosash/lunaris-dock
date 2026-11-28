using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Lunaris
{
    public sealed class DesktopUi : IDisposable
    {
        internal readonly PlatformServices Platform;
        internal readonly UserSettings Settings;
        private readonly Action save;
        private readonly Action exit;
        private readonly Dispatcher dispatcher;
        private sealed class Surface
        {
            internal DockSurface Dock;
            internal ScreenInfo Screen;
            internal string RenderKey;
            internal long RevealUntil;
        }
        private readonly Dictionary<string, Surface> surfaces = new Dictionary<string, Surface>(StringComparer.OrdinalIgnoreCase);
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private HashSet<string> covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private long nextCoverCheck;
        private Point previousCursor;
        private bool hasPreviousCursor;
        internal ScreenInfo[] Monitors { get; private set; }
        internal ScreenInfo ActiveMonitor { get; private set; }
        internal double DockTopPixels
        {
            get
            {
                Surface surface; double top;
                if (surfaces.TryGetValue(ActiveMonitor.Id, out surface) && ScreenInfo.TryGetTop(surface.Dock, out top)) return top;
                var area = Settings.ReplaceTaskbar ? ActiveMonitor.Bounds : ActiveMonitor.WorkArea;
                return area.Bottom - (Settings.Size + 24) * ActiveMonitor.Scale;
            }
        }
        private LaunchSurface launchpad;
        private Preferences preferences;
        internal ReleaseInfo AvailableUpdate { get; private set; }
        private readonly ReleaseUpdates releases = new ReleaseUpdates();
        private DispatcherTimer releaseTimer;
        private DispatcherTimer timer, pointerTimer;
        private DispatcherTimer pendingSizeSave;
        private bool disposed, reading, reread;
        internal string CatalogError;
        internal bool CatalogLoading { get { return reading; } }
        internal List<AppEntry> Catalog = new List<AppEntry>();
        internal List<WindowEntry> Windows = new List<WindowEntry>();
        private Dictionary<string,AppEntry> byId = new Dictionary<string,AppEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AppEntry> filePins = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);

        public DesktopUi(PlatformServices platform, UserSettings settings, Action save, Action exit)
        {
            Platform = platform; Settings = settings; this.save = save; this.exit = exit;
            dispatcher = Dispatcher.CurrentDispatcher;
            Monitors = new ScreenInfo[0];
        }

        public void Start()
        {
            Platform.ShellChanged += OnShellChanged;
            Platform.ToggleRequested += OnToggle;
            Platform.WindowsStartRequested += ShowLaunchpad;
            UpdateWindows();
            ReloadCatalog();
            timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(1150) };
            timer.Tick += delegate { UpdateWindows(); };
            timer.Start();
            pointerTimer = new DispatcherTimer(DispatcherPriority.Input, dispatcher) { Interval = TimeSpan.FromMilliseconds(60) };
            pointerTimer.Tick += delegate { UpdateVisibility(true); };
            pointerTimer.Start();
            releaseTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromHours(6) };
            releaseTimer.Tick += RefreshUpdate;
            RefreshUpdate(this, EventArgs.Empty);
        }

        // Invoked on the WPF dispatcher. Schedule the next check after this one
        // finishes so a slow or unavailable service cannot produce overlapping polls.
        private async void RefreshUpdate(object sender, EventArgs args)
        {
            releaseTimer.Stop();
            ReleaseInfo latest;
            try { latest = await Task.Run<ReleaseInfo>(new Func<Task<ReleaseInfo>>(releases.LatestAsync)); }
            catch (HttpRequestException) { return; }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SerializationException) { return; }
            catch (System.Xml.XmlException) { return; }
            catch (IOException) { return; }
            finally { if (!disposed) releaseTimer.Start(); }
            SetAvailableUpdate(latest);
        }

        private void SetAvailableUpdate(ReleaseInfo latest)
        {
            if (disposed || latest == AvailableUpdate) return;
            if (AvailableUpdate != null && latest != null && AvailableUpdate.Url == latest.Url) return;
            AvailableUpdate = latest;
            InvalidateDocks();
            RenderDocks(false);
            if (preferences != null) preferences.RefreshUpdates();
        }

        private void OnShellChanged()
        {
            if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(new Action(delegate {
                if (!disposed) { UpdateWindows(); ReloadCatalog(); }
            }));
        }

        private void OnToggle()
        {
            if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(new Action(ToggleLaunchpad));
        }

        internal void ReloadCatalog()
        {
            if (reading) { reread = true; return; }
            reading = true;
            CatalogError = null;
            var worker = new Thread(delegate() {
                List<AppEntry> apps = null;
                string error = null;
                try
                {
                    apps = Platform.GetApplications();
                    foreach (var app in apps)
                        if (app.Icon != null && !app.Icon.IsFrozen) {
                            if (app.Icon.CanFreeze) app.Icon.Freeze(); else app.Icon = null;
                        }
                    apps = apps.Where(a => !String.IsNullOrWhiteSpace(a.Id) && !String.IsNullOrWhiteSpace(a.Name))
                        .GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                        .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                }
                catch (Exception ex) { error = ex.Message; }
                if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(new Action(delegate {
                    if (disposed) return;
                    reading = false;
                    CatalogError = error;
                    if (error == null && apps != null)
                    {
                        Catalog = apps;
                        byId = apps.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
                        filePins.Clear();
                        CanonicalizePins();
                        ChooseInitialPins();
                    }
                    if (launchpad != null) launchpad.RefreshCatalog();
                    InvalidateDocks(); UpdateWindows();
                    if (reread) { reread = false; ReloadCatalog(); }
                }));
            });
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }

        private void CanonicalizePins()
        {
            // An executable pin and a previously pinned AppsFolder entry can refer to
            // the same app. Preserve the executable launch target and the first slot.
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Settings.Pins.Where(ShellCatalog.IsExecutable))
            {
                files[path] = path;
                files[Platform.PinIdentity(path)] = path;
            }
            var pins = new List<string>();
            foreach (var id in Settings.Pins)
            {
                string path;
                string stored = files.TryGetValue(Platform.PinTarget(id), out path) ? path : id;
                if (!pins.Any(p => IdEqual(p, stored))) pins.Add(stored);
            }
            if (!Settings.Pins.SequenceEqual(pins, StringComparer.OrdinalIgnoreCase))
            {
                Settings.Pins = pins;
                Persist();
            }
        }

        private void ChooseInitialPins()
        {
            if (!Settings.InitializePins || Catalog.Count == 0) return;
            if (Settings.Pins.Count == 0)
            {
                string[][] groups = {
                    new[] {"Проводник","File Explorer","Windows Explorer"},
                    new[] {"Microsoft Edge","Google Chrome","Firefox","Яндекс Браузер","Yandex"},
                    new[] {"Параметры","Settings"},
                    new[] {"Фотографии","Photos","Microsoft Photos"},
                    new[] {"Терминал","Terminal","Windows Terminal","Windows PowerShell"},
                    new[] {"Блокнот","Notepad"},
                    new[] {"Калькулятор","Calculator","Windows Calculator"},
                    new[] {"Paint","Microsoft Paint","Рисование"},
                    new[] {"Microsoft Store","Магазин Microsoft","Store"},
                    new[] {"Медиаплеер","Медиапроигрыватель","Media Player","Windows Media Player","Часы","Clock","Windows Clock"}
                };
                foreach (var names in groups)
                {
                    var entry = names.Select(n => Catalog.FirstOrDefault(a => String.Equals(a.Name, n,
                        StringComparison.CurrentCultureIgnoreCase))).FirstOrDefault(a => a != null);
                    if (entry != null) Settings.Pins.Add(entry.Id);
                }
            }
            if (Settings.Pins.Count == 0) return;
            Settings.InitializePins = false;
            Persist();
        }

        private void UpdateWindows()
        {
            if (disposed) return;
            try
            {
                Windows = Platform.GetWindows();
                // A window can advertise a different AppUserModelID than the primary
                // Start entry for its executable. Group it with an explicit file pin.
                foreach (var window in Windows)
                {
                    if (window.ProcessPath == null || !Settings.Pins.Contains(window.ProcessPath, StringComparer.OrdinalIgnoreCase)) continue;
                    var app = FindApp(window.ProcessPath);
                    if (app != null) { window.AppId = app.Id; window.App = app; }
                }
                bool newTheme = Appearance.RefreshFor(Settings);
                bool newLayout = SyncScreens();
                if (newTheme)
                {
                    foreach (var entry in surfaces.Values) ThemeTransition.Begin(entry.Dock);
                    ThemeTransition.Begin(launchpad);
                    ThemeTransition.Begin(preferences);
                    foreach (var entry in surfaces.Values) entry.Dock.ApplyTheme();
                    if (launchpad != null) launchpad.ApplyTheme();
                    if (preferences != null) preferences.ApplyTheme();
                    InvalidateDocks();
                }
                RenderDocks(newLayout);
                nextCoverCheck = 0;
                UpdateVisibility(true);
            }
            catch (System.ComponentModel.Win32Exception) { }
        }

        private void RenderDocks(bool newLayout)
        {
            var key = String.Join("\n", Windows.Select(w => w.AppId).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
            key += "|" + Settings.ReplaceTaskbar + "|" + Settings.Size + "|" + String.Join("\n", Settings.Pins);
            bool anchorChanged = false;
            foreach (var entry in surfaces.Values)
            {
                if (entry.RenderKey == key || entry.Dock.Busy) continue;
                entry.Dock.Render(entry.Screen);
                entry.RenderKey = key;
                if (IdEqual(entry.Screen.Id, ActiveMonitor.Id)) anchorChanged = true;
            }
            if (launchpad != null && launchpad.IsVisible && (anchorChanged || newLayout))
                launchpad.Reposition(ActiveMonitor);
        }

        private void InvalidateDocks() { foreach (var entry in surfaces.Values) entry.RenderKey = null; }

        internal static ScreenInfo[] SelectMonitors(ScreenInfo[] available, string preference)
        {
            if (available.Length == 0 || IdEqual(preference, "all")) return available;
            var exact = available.FirstOrDefault(s => IdEqual(s.Id, preference));
            return new[] { exact ?? available.FirstOrDefault(s => s.Primary) ?? available[0] };
        }

        private bool SyncScreens()
        {
            var screens = ScreenInfo.Read();
            bool changed = screens.Length != Monitors.Length ||
                screens.Where((s,i) => !s.SameLayout(Monitors[i])).Any();
            if (changed) Monitors = screens;
            var selected = SelectMonitors(Monitors, Settings.Monitor);
            ActiveMonitor = ActiveMonitor == null ? selected[0] :
                selected.FirstOrDefault(s => IdEqual(s.Id, ActiveMonitor.Id)) ?? selected[0];
            foreach (var stale in surfaces.Keys.Where(id => !selected.Any(s => IdEqual(s.Id,id))).ToArray())
            {
                surfaces[stale].Dock.Close();
                surfaces.Remove(stale);
            }
            foreach (var screen in selected)
            {
                Surface entry;
                if (!surfaces.TryGetValue(screen.Id, out entry))
                {
                    entry = new Surface { Dock = new DockSurface(this, screen), Screen = screen };
                    surfaces.Add(screen.Id, entry);
                }
                if (!entry.Screen.SameLayout(screen)) { entry.Screen = screen; entry.RenderKey = null; }
            }
            if (changed && preferences != null) preferences.RefreshMonitors();
            return changed;
        }

        private ScreenInfo PointerMonitor()
        {
            var selected = SelectMonitors(Monitors, Settings.Monitor);
            Point cursor;
            if (ScreenInfo.TryPointer(out cursor))
            {
                var found = selected.FirstOrDefault(s => s.Contains(cursor));
                if (found != null) return found;
            }
            return selected.FirstOrDefault();
        }

        private void UpdateVisibility(bool animate)
        {
            if (disposed || dispatcher.HasShutdownStarted || surfaces.Count == 0) return;
            long now = clock.ElapsedMilliseconds;
            bool checkCover = now >= nextCoverCheck;
            if (checkCover)
            {
                covered = Platform.CoveredScreens(Monitors);
                nextCoverCheck = now + 280;
            }
            Point cursor;
            bool known = ScreenInfo.TryPointer(out cursor);
            foreach (var entry in surfaces.Values)
            {
                var screen = entry.Screen;
                var dock = entry.Dock;
                if (known)
                {
                    bool edge = screen.AtBottom(hasPreviousCursor ? previousCursor : cursor, cursor);
                    if (!Settings.ReplaceTaskbar && screen.Contains(cursor) &&
                        Math.Abs(cursor.Y - (screen.WorkArea.Bottom - 1)) <= 2 * screen.Scale) edge = true;
                    if (edge) entry.RevealUntil = now + 640;
                    else if (dock.IsRevealed && ScreenInfo.InDockCorridor(dock, cursor, screen))
                        entry.RevealUntil = now + 220;
                }
                bool menuOpen = launchpad != null && launchpad.IsVisible && IdEqual(ActiveMonitor.Id,screen.Id);
                bool keepVisible = !Settings.HideDockWhenCovered || (preferences != null && preferences.IsVisible);
                bool show = keepVisible || menuOpen || !covered.Contains(screen.Id) || dock.WantsPointer || dock.Busy || now < entry.RevealUntil;
                dock.SetRevealed(show, animate);
                if (keepVisible && checkCover && covered.Contains(screen.Id)) dock.KeepAboveForeground();
            }
            hasPreviousCursor = known;
            previousCursor = cursor;
        }

        public void ToggleLaunchpad()
        {
            if (disposed) return;
            if (launchpad != null && launchpad.IsOpen) launchpad.Dismiss();
            else ShowLaunchpad();
        }

        internal void ToggleLaunchpad(ScreenInfo screen)
        {
            if (disposed) return;
            if (launchpad != null && launchpad.IsOpen && IdEqual(ActiveMonitor.Id, screen.Id)) launchpad.Dismiss();
            else ShowLaunchpad(screen);
        }

        public void ShowLaunchpad() { ShowLaunchpad(PointerMonitor()); }

        internal void ShowLaunchpad(ScreenInfo screen)
        {
            if (disposed || screen == null) return;
            ActiveMonitor = screen;
            if (preferences != null) preferences.Close();
            if (launchpad == null) launchpad = new LaunchSurface(this);
            Surface entry;
            if (surfaces.TryGetValue(screen.Id, out entry)) entry.Dock.SetRevealed(true, false);
            launchpad.Open(screen);
        }

        internal void LaunchpadClosed() { UpdateVisibility(true); }

        internal AppEntry FindApp(string id)
        {
            AppEntry entry;
            if (ShellCatalog.IsExecutable(id))
            {
                if (!filePins.TryGetValue(id, out entry))
                {
                    entry = Platform.ResolveExecutablePin(id);
                    if (entry != null) filePins[id] = entry;
                }
                return entry;
            }
            if (byId.TryGetValue(id, out entry)) return entry;
            var window = Windows.FirstOrDefault(w => IdEqual(w.AppId, id) && w.App != null);
            if (window != null) return window.App;
            entry = Platform.ResolveApp(id);
            if (entry != null) byId[id] = entry;
            return entry;
        }

        internal static bool IdEqual(string a, string b) { return String.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        internal string PinKey(string id)
        {
            return Settings.Pins.FirstOrDefault(pin => IdEqual(pin, id) || IdEqual(Platform.PinIdentity(pin), id));
        }
        internal bool Pinned(string id) { return PinKey(id) != null; }
        internal List<WindowEntry> WindowsFor(string id) { return Windows.Where(w => IdEqual(w.AppId, id)).ToList(); }

        internal void TogglePin(AppEntry app)
        {
            string key = PinKey(app.Id);
            if (key != null) Settings.Pins.RemoveAll(pin => IdEqual(pin, key));
            else Settings.Pins.Add(app.Id);
            Settings.InitializePins = false;
            Changed();
        }

        internal void DropPin(string source, string target, bool after)
        {
            if (IdEqual(source, target) || FindApp(source) == null) return;
            Settings.Pins.RemoveAll(pin => IdEqual(pin, source));
            int index = Settings.Pins.FindIndex(pin => IdEqual(pin, target));
            if (index < 0) Settings.Pins.Add(source);
            else Settings.Pins.Insert(index + (after ? 1 : 0), source);
            Settings.InitializePins = false;
            Changed();
        }

        internal void DropFiles(string[] pins, string anchor, bool after)
        {
            if (pins.Length == 0) return;
            var order = Settings.Pins;
            int at = order.FindIndex(id => IdEqual(id, anchor));
            at = at < 0 ? order.Count : at + (after ? 1 : 0);
            var moved = new HashSet<string>(pins, StringComparer.OrdinalIgnoreCase);
            moved.UnionWith(pins.Select(Platform.PinIdentity));
            Func<string,bool> keep = id => !moved.Contains(id) && !moved.Contains(Platform.PinTarget(id));
            // Compute both sides before removing a duplicate (possibly the anchor).
            var result = order.Take(at).Where(keep).Concat(pins).Concat(order.Skip(at).Where(keep)).ToList();
            if (order.SequenceEqual(result, StringComparer.OrdinalIgnoreCase)) return;
            Settings.Pins = result;
            Settings.InitializePins = false;
            Changed();
        }

        private void Persist()
        {
            try { save(); }
            catch (Exception ex) { Alert(Loc.Get("ApplyError"), ex); }
        }

        internal void PersistLibrary() { Persist(); }

        internal void ResizeDock(double size)
        {
            if (Settings.Size == size) return;
            Settings.Size = size;
            RenderDocks(false);
            if (pendingSizeSave == null)
            {
                pendingSizeSave = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(175) };
                pendingSizeSave.Tick += delegate { FlushSize(); };
            }
            pendingSizeSave.Stop();
            pendingSizeSave.Start();
        }

        private void FlushSize()
        {
            if (pendingSizeSave == null || !pendingSizeSave.IsEnabled) return;
            pendingSizeSave.Stop();
            Persist();
        }

        internal void ChangeLanguage(string code)
        {
            if (code == Settings.Language) return;
            Settings.Language = code;
            Loc.Use(code);
            // Preferences and Start are mutually exclusive. Rebuild the cached Start
            // window on its next open so all its controls use the selected language.
            if (launchpad != null) { launchpad.Close(); launchpad = null; }
            if (preferences != null) preferences.ApplyLanguage();
            Changed();
        }

        internal void Changed()
        {
            if (pendingSizeSave != null) pendingSizeSave.Stop();
            Persist();
            InvalidateDocks();
            UpdateWindows();
        }

        internal void OpenPreferences() { OpenPreferences(ActiveMonitor ?? PointerMonitor()); }

        internal void OpenPreferences(ScreenInfo screen)
        {
            if (disposed) return;
            if (screen != null) ActiveMonitor = screen;
            if (launchpad != null && launchpad.IsVisible) launchpad.Dismiss();
            if (preferences == null)
            {
                preferences = new Preferences(this);
                preferences.Closed += delegate { preferences = null; FlushSize(); UpdateVisibility(true); };
            }
            preferences.Present();
            UpdateVisibility(true);
            preferences.Activate();
            Platform.FocusWindow(new WindowInteropHelper(preferences).Handle);
        }

        internal void Launch(AppEntry app)
        {
            if (launchpad != null && launchpad.IsVisible) launchpad.Dismiss();
            try { Platform.Launch(app); }
            catch (Exception ex) { Alert(Loc.Format("LaunchError", app.Name), ex); }
        }

        internal void Activate(WindowEntry window)
        {
            try { Platform.Activate(window); }
            catch (Exception ex) { Alert(Loc.Get("SwitchWindowError"), ex); }
        }

        private void Alert(string message, Exception ex)
        {
            MessageBox.Show(message + "\n\n" + ex.Message, "Lunaris Dock", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        internal void Exit()
        {
            if (disposed) return;
            // Window.Closed may run before the dispatcher's ShutdownStarted event.
            // Stop the UI while it still owns all its windows, then end the WPF app.
            Dispose();
            exit();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (releaseTimer != null) releaseTimer.Stop();
            releases.Dispose();
            Platform.ShellChanged -= OnShellChanged;
            Platform.ToggleRequested -= OnToggle;
            Platform.WindowsStartRequested -= ShowLaunchpad;
            FlushSize();
            if (timer != null) timer.Stop();
            if (pointerTimer != null) pointerTimer.Stop();
            if (preferences != null) preferences.CloseImmediately();
            if (launchpad != null) launchpad.Close();
            foreach (var entry in surfaces.Values) entry.Dock.Close();
            surfaces.Clear();
        }
    }
}
