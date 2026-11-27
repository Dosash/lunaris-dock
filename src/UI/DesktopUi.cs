using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
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
        internal readonly Action Exit;
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
        private DispatcherTimer timer, pointerTimer;
        private DispatcherTimer pendingSizeSave;
        private bool disposed, reading, reread;
        internal string CatalogError;
        internal bool CatalogLoading { get { return reading; } }
        internal List<AppEntry> Catalog = new List<AppEntry>();
        internal List<WindowEntry> Windows = new List<WindowEntry>();
        private Dictionary<string,AppEntry> byId = new Dictionary<string,AppEntry>(StringComparer.OrdinalIgnoreCase);

        public DesktopUi(PlatformServices platform, UserSettings settings, Action save, Action exit)
        {
            Platform = platform; Settings = settings; this.save = save; Exit = exit;
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
            // An unregistered executable may gain a shell shortcut after it was pinned.
            // Window grouping and the pin must use the same current application identity.
            var pins = new List<string>();
            foreach (var id in Settings.Pins)
            {
                var app = Platform.ResolveApp(id);
                string canonical = app == null ? id : app.Id;
                if (!pins.Any(p => IdEqual(p, canonical))) pins.Add(canonical);
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
            if (disposed || surfaces.Count == 0) return;
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
            if (byId.TryGetValue(id, out entry)) return entry;
            var window = Windows.FirstOrDefault(w => IdEqual(w.AppId, id) && w.App != null);
            if (window != null) return window.App;
            entry = Platform.ResolveApp(id);
            if (entry != null) byId[id] = entry;
            return entry;
        }

        internal static bool IdEqual(string a, string b) { return String.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        internal bool Pinned(string id) { return Settings.Pins.Any(pin => IdEqual(pin, id)); }
        internal List<WindowEntry> WindowsFor(string id) { return Windows.Where(w => IdEqual(w.AppId, id)).ToList(); }

        internal void TogglePin(AppEntry app)
        {
            if (Pinned(app.Id)) Settings.Pins.RemoveAll(pin => IdEqual(pin, app.Id));
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

        private void Persist()
        {
            try { save(); }
            catch (Exception ex) { Alert("Не удалось применить настройки", ex); }
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
            catch (Exception ex) { Alert("Не удалось открыть «" + app.Name + "»", ex); }
        }

        internal void Activate(WindowEntry window)
        {
            try { Platform.Activate(window); }
            catch (Exception ex) { Alert("Не удалось переключиться на окно", ex); }
        }

        private void Alert(string message, Exception ex)
        {
            MessageBox.Show(message + ".\n\n" + ex.Message, "Lunaris Dock", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        public void Dispose()
        {
            disposed = true;
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
