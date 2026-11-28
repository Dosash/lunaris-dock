using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lunaris;
using Microsoft.Win32;

internal class CoreTests
{
    static void Require(bool pass, string label)
    {
        if (!pass) throw new Exception(label);
        Console.WriteLine("PASS " + label);
    }

    static void Settings(string profile)
    {
        Environment.SetEnvironmentVariable("LUNARIS_PROFILE", profile);
        var languages = new[] { "en", "ru", "de", "es", "zh-CN" };
        CheckLanguageResources(languages);
        var first = SettingsStore.Load();
        Require(first.ShowStatusBar && first.IconDesigns.Count == 0, "new profiles show the status bar and retain original artwork");
        var defaultLanguage = first.Language;
        Require(first.ShowUpdateBadge, "new profile shows available release badges");
        Require(languages.Contains(defaultLanguage) && !first.LiquidGlass, "new profile has a supported language and standard material");
        Require(first.HideDockWhenCovered && !first.HideWindowsStart,"new shell options default to automatic Dock hiding and native Start unchanged");
        Require(first.Size == 48 && first.Magnify && !first.Startup && !first.ReplaceTaskbar, "safe settings on first launch");
        first.Size = 1000;
        first.Pins.AddRange(new[] {"pinned-b", "pinned-A", "PINNED-a", " "});
        first.ReplaceTaskbar = true;
        first.Startup = true;
        first.Magnify = false;
        first.InitializePins = false;
        first.Theme = "dark";
        first.Language = "zh-CN";
        first.LiquidGlass = true;
        first.ShowUpdateBadge = false;
        first.ShowStatusBar = false;
        first.Monitor = "saved-external-display";
        first.HideDockWhenCovered=false;
        first.HideWindowsStart=true;
        SettingsStore.Save(first);
        var restored = SettingsStore.Load();
        Require(!restored.ShowStatusBar, "status bar visibility persists after restart");
        Require(!restored.ShowUpdateBadge, "disabled release badge survives a profile round-trip");
        Require(restored.Language == "zh-CN" && restored.LiquidGlass, "language and Liquid Glass survive a profile round-trip");
        Require(restored.Pins.SequenceEqual(new[] {"pinned-b","pinned-A"}), "deduplication preserves pin order");
        Require(restored.Size == 80 && restored.ReplaceTaskbar && restored.Startup && !restored.Magnify, "saved preferences round-trip and range");
        Require(restored.Theme == "dark" && restored.Monitor == "saved-external-display", "theme and display identity round-trip");
        Require(!restored.HideDockWhenCovered && restored.HideWindowsStart,"shell options round-trip independently");
        restored.Pins.Clear();
        restored.Size = double.NaN;
        SettingsStore.Save(restored);
        Require(SettingsStore.Load().Pins.Count == 0 && !SettingsStore.Load().InitializePins && SettingsStore.Load().Size == 48, "user can unpin every item");
        var path = Path.Combine(profile, "settings.json");
        using (var locked = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Require(SettingsStore.Load().Pins.Count == 2, "read backup when the main file is exclusively locked");
        File.WriteAllText(path, "{broken");
        Require(SettingsStore.Load().Pins.Count == 2, "last valid backup after truncated settings");
        File.WriteAllText(path, "{\"Pins\":[\"legacy\"]}");
        var old = SettingsStore.Load();
        Require(old.ShowStatusBar && old.IconDesigns.Count == 0, "existing profiles gain the status bar and an empty icon collection");
        Require(old.ShowUpdateBadge, "pre-update profiles default to showing release badges");
        Require(old.Language == defaultLanguage && !old.LiquidGlass, "old profile inherits the installation or Windows language and keeps standard material");
        Require(old.Magnify && old.Size == 48 && old.Pins.Single() == "legacy", "migrate partial settings");
        File.WriteAllText(path, "{\"Pins\":[\"old-b\",\"old-a\"],\"Startup\":true,\"ReplaceTaskbar\":true,\"Size\":46,\"Magnify\":false,\"InitializePins\":false}");
        old = SettingsStore.Load();
        Require(old.Theme == "system" && old.Monitor == "primary" && old.Pins.SequenceEqual(new[]{"old-b","old-a"}) &&
                old.Size==46 && old.Startup && old.ReplaceTaskbar && !old.Magnify && !old.InitializePins,
                "1.0 profile migrates to system theme and primary monitor while retaining old preferences");
        Require(old.HideDockWhenCovered && !old.HideWindowsStart && old.Library!=null,"older profiles preserve automatic hiding and get an empty library overlay");
        Require(old.CustomSurface==UserSettings.DefaultCustomSurface && old.CustomText==UserSettings.DefaultCustomText &&
                old.CustomAccent==UserSettings.DefaultCustomAccent,"older profiles receive the three custom palette defaults");
        File.WriteAllText(path,"{\"Theme\":\" Custom \",\"CustomSurface\":\"  #2b2844 \",\"CustomText\":null,\"CustomAccent\":\"#AAZZ22\"}");
        var custom=SettingsStore.Load();
        Require(custom.Theme=="custom" && custom.CustomSurface=="#2B2844" &&
                custom.CustomText==UserSettings.DefaultCustomText && custom.CustomAccent==UserSettings.DefaultCustomAccent,
                "custom theme and lowercase RGB normalize while malformed colors fall back independently");
        custom.CustomSurface="#302040"; custom.CustomText="#eeeedd"; custom.CustomAccent="#aabbff";
        SettingsStore.Save(custom);
        custom=SettingsStore.Load();
        Require(custom.Theme=="custom" && custom.CustomSurface=="#302040" && custom.CustomText=="#EEEEDD" &&
                custom.CustomAccent=="#AABBFF","all three explicit custom colors survive an atomic profile round-trip");
        File.WriteAllText(path, "{\"Language\":\"invalid\",\"Theme\":\"invalid\",\"Monitor\":\"\",\"Pins\":[]}");
        old = SettingsStore.Load();
        Require(languages.Contains(old.Language), "unknown language resolves to a supported translation");
        Require(old.Theme=="system" && old.Monitor=="primary","unknown theme and empty monitor resolve to defaults");
        File.WriteAllText(path, "{");
        File.WriteAllText(path + ".bak", "{");
        Require(!SettingsStore.Load().ReplaceTaskbar, "both files damaged: safe defaults");
    }

    static void CheckLanguageResources(string[] languages)
    {
        Dictionary<string, string> reference = null;
        foreach (string language in languages)
        {
            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var stream = typeof(UserSettings).Assembly.GetManifestResourceStream("Lunaris.Languages." + language + ".txt"))
            using (var reader = new StreamReader(stream, new UTF8Encoding(false, true)))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (String.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
                    int split = line.IndexOf('=');
                    if (split <= 0 || String.IsNullOrWhiteSpace(line.Substring(split + 1)))
                        throw new Exception("Missing translation in " + language + ": " + line);
                    string value = line.Substring(split + 1);
                    var placeholders = Regex.Matches(value, @"\{(\d+)[^}]*\}").Cast<Match>()
                        .Select(m => m.Groups[1].Value).Distinct().OrderBy(id => id);
                    entries.Add(line.Substring(0, split), String.Join(",", placeholders));
                }
            }
            if (reference == null) reference = entries;
            Require(entries.Count > 0 && entries.Count == reference.Count && reference.All(pair =>
                entries.ContainsKey(pair.Key) && entries[pair.Key] == pair.Value),
                "complete embedded translations and format placeholders for " + language);
        }
    }

    static void Library()
    {
        var apps = new[] { new AppEntry {Id="one",Name="Alpha",StartFolder=@"Vendor\Tools"},
                           new AppEntry {Id="two",Name="Beta",StartFolder="Vendor"},
                           new AppEntry {Id="three",Name="Gamma"} };
        var settings=new UserSettings();
        settings.Pins.Add("two");
        var lib=new LaunchLibrary(settings,apps);
        const string root=LaunchLibrary.Root;
        string vendor="start:Vendor", tools=@"start:Vendor\Tools";
        string one=LaunchLibrary.AppKey("one"), two=LaunchLibrary.AppKey("two"), three=LaunchLibrary.AppKey("three");
        Require(lib.Find(tools).ParentId==vendor && lib.Find(one).ParentId==tools && lib.Find(vendor).IsSystem,
                "nested Start folders and application parents are represented in the library");
        Require(!lib.RenameFolder(vendor,"Other") && !lib.DeleteFolder(tools),"native Start folders are protected from rename and deletion");
        string custom=lib.CreateFolder("Project",root);
        string inside=lib.CreateFolder("Drafts",custom);
        Require(custom!=null && inside!=null && lib.CreateFolder(" project ",root)==null &&
                !lib.Move(custom,inside) && !lib.Move(inside,inside),"custom folder creation rejects duplicate labels and hierarchy cycles");
        Require(lib.Move(one,custom,inside) && lib.Move(two,custom,one) &&
                lib.Children(custom).Select(i=>i.Id).SequenceEqual(new[]{two,one,inside}) &&
                lib.ManualOrder(custom),"manual ordering interleaves programs and folders");
        Require(lib.RenameFolder(custom,"Work") && lib.Move(tools,root) && lib.Move(vendor,tools) && lib.Move(tools,custom),
                "reparent a native folder and its former ancestor locally");
        SettingsStore.Save(settings);
        settings=SettingsStore.Load();
        lib=new LaunchLibrary(settings,apps);
        Require(lib.Find(custom).Name=="Work" && lib.Find(vendor).ParentId==tools && lib.Find(tools).ParentId==custom &&
                lib.Find(one).ParentId==custom && settings.Pins.SequenceEqual(new[]{"two"}),
                "folder identity, order and parent graph survive restart independently of Dock pins");
        var offline=new LaunchLibrary(settings,apps.Where(a=>a.Id!="two"));
        offline.Move(three,custom,one);
        lib=new LaunchLibrary(settings,apps);
        Require(lib.Children(custom).Select(i=>i.Id).Take(3).SequenceEqual(new[]{two,three,one}),
                "manual order retains unavailable application slots when a new app is moved");
        Require(lib.SortByName(custom) && !lib.ManualOrder(custom) && lib.Children(custom).First().IsFolder &&
                lib.SetManualOrder(custom) && lib.ManualOrder(custom),"alphabetical and explicit manual ordering can be switched");
        Require(lib.DeleteFolder(custom) && lib.Find(one).ParentId==root && lib.Find(inside).ParentId==root &&
                lib.Find(tools).ParentId==root && lib.Find(vendor).ParentId==tools && lib.Find(custom)==null,
                "deleting a custom folder returns its contents without deleting applications or subfolders");
        Require(apps[0].StartFolder==@"Vendor\Tools" && apps[1].StartFolder=="Vendor","organizing the library never modifies imported shortcut data");
    }

    const BindingFlags InternalInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    const BindingFlags InternalStatic = BindingFlags.Static | BindingFlags.NonPublic;
    [DllImport("advapi32.dll")]
    static extern int RegOverridePredefKey(IntPtr key, IntPtr replacement);

    static void InstallLanguage(RegistryKey sandbox)
    {
        var detect = typeof(UserSettings).Assembly.GetType("Lunaris.Loc", true).GetMethod("DetectDefault", InternalStatic);
        var previous = Thread.CurrentThread.CurrentUICulture;
        try
        {
            Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("es-MX");
            Require((string)detect.Invoke(null, null) == "es", "uninstalled copy uses Windows UI language");
            using (var key = sandbox.CreateSubKey(@"Software\LunarisDock", RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryOptions.Volatile))
            {
                key.SetValue("InstallLanguage", "zhCN");
                Require((string)detect.Invoke(null, null) == "zh-CN", "Chinese installer code selects the simplified Chinese catalogue");
                key.SetValue("InstallLanguage", "de");
                Require((string)detect.Invoke(null, null) == "de", "installed language takes precedence over Windows UI language");
            }
        }
        finally { Thread.CurrentThread.CurrentUICulture = previous; }
    }

    static void Themes()
    {
        var appearance = typeof(DesktopUi).Assembly.GetType("Lunaris.Appearance",true);
        var refresh = appearance.GetMethod("Refresh",InternalStatic);
        var dark = appearance.GetProperty("Dark",InternalStatic);
        Func<string,bool> apply = mode => (bool)refresh.Invoke(null,new object[]{mode});
        Func<bool> isDark = () => (bool)dark.GetValue(null,null);
        const string preferences = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        string name = "LunarisThemeTest-" + Guid.NewGuid().ToString("N");
        IntPtr hkcu = new IntPtr(unchecked((int)0x80000001));
        // The application reads its normal registry path. Only this test process sees the
        // private volatile tree instead of the user's HKCU; no desktop setting is changed.
        try
        {
            using (var sandbox = Registry.CurrentUser.CreateSubKey(name,RegistryKeyPermissionCheck.ReadWriteSubTree,RegistryOptions.Volatile))
            using (var simulated = sandbox.CreateSubKey(preferences,RegistryKeyPermissionCheck.ReadWriteSubTree,RegistryOptions.Volatile))
            {
                simulated.SetValue("AppsUseLightTheme",1,RegistryValueKind.DWord);
                int error = RegOverridePredefKey(hkcu,sandbox.Handle.DangerousGetHandle());
                if (error!=0) throw new System.ComponentModel.Win32Exception(error);
                try
                {
                    InstallLanguage(sandbox);
                    apply("dark"); apply("system");
                    Require(!isDark(),"system mode follows the light Windows app preference");
                    simulated.SetValue("AppsUseLightTheme",0,RegistryValueKind.DWord);
                    var limit=System.Diagnostics.Stopwatch.StartNew();
                    while (!isDark() && limit.ElapsedMilliseconds<3000) { Thread.Sleep(100); apply("system"); }
                    Require(isDark(),"unchanged system mode tracks a live Windows app-theme switch");
                    apply("light");
                    Thread.Sleep(720);
                    apply("light");
                    Require(!isDark(),"explicit light appearance stays selected against dark Windows preference");
                    var settings = new UserSettings { Theme = "light", LiquidGlass = true };
                    var refreshFor = appearance.GetMethod("RefreshFor", InternalStatic);
                    var glass = appearance.GetProperty("Glass", InternalStatic);
                    simulated.SetValue("EnableTransparency", 1, RegistryValueKind.DWord);
                    refreshFor.Invoke(null, new object[] {settings});
                    Require((glass.GetValue(null,null) != null) == !SystemParameters.HighContrast,
                        "glass material follows the enabled Windows transparency and high-contrast settings");
                    if (!SystemParameters.HighContrast) CheckStartGlass(settings);
                    simulated.SetValue("EnableTransparency", 0, RegistryValueKind.DWord);
                    refreshFor.Invoke(null, new object[] {settings});
                    Require(glass.GetValue(null,null) == null && settings.LiquidGlass,
                        "turning off Windows transparency restores the standard surface and retains the preference");
                }
                finally
                {
                    error = RegOverridePredefKey(hkcu,IntPtr.Zero);
                    if (error!=0) throw new System.ComponentModel.Win32Exception(error);
                }
            }
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(name,false); }
        apply("dark"); apply("system");
    }

    static void CustomThemes()
    {
        var type = typeof(DesktopUi).Assembly.GetType("Lunaris.Appearance",true);
        var refresh = type.GetMethod("RefreshFor",InternalStatic);
        Func<UserSettings,bool> apply = s => (bool)refresh.Invoke(null,new object[]{s});
        Func<string,bool> flag = name => (bool)type.GetProperty(name,InternalStatic).GetValue(null,null);
        var roles = new[]{"Surface","Text","Accent","Panel","Border","Muted","Selection"};
        Func<SolidColorBrush[]> brushes = () => roles.Select(name =>
            (SolidColorBrush)type.GetProperty(name,InternalStatic).GetValue(null,null)).ToArray();
        var choice=new UserSettings { Theme="custom",CustomSurface="#212938",CustomText="#F9F5FF",CustomAccent="#B6A0FF" };
        apply(new UserSettings {Theme="dark"});
        Require(apply(choice) && flag("Custom") && flag("Dark"),"custom colors replace the built-in palette at the same luminance");
        var dark=brushes();
        Require(dark[0].Color==Color.FromRgb(33,41,56) && dark[1].Color==Color.FromRgb(249,245,255) &&
                dark[2].Color==Color.FromRgb(182,160,255) && dark.All(b=>b.IsFrozen && !b.HasAnimatedProperties && b.Color.A==255) &&
                dark.Select(b=>b.Color).Distinct().Count()==dark.Length,
                "explicit and derived custom roles use distinct opaque frozen endpoint brushes");
        choice.CustomAccent=" #b6a0ff ";
        Require(!apply(choice) && dark.SequenceEqual(brushes()),"equivalent colors retain shared brush identity on refresh");
        choice.CustomAccent="#00CA80";
        Require(apply(choice),"editing only the accent immediately updates a custom theme");
        var edited=brushes();
        Require(edited[6].Color!=dark[6].Color && edited[3].Color==dark[3].Color &&
                Enumerable.Range(0,roles.Length).All(i=>!Object.ReferenceEquals(edited[i],dark[i])),
                "accent rebuilds the selected tint and publishes a fresh complete brush set");
        choice.CustomSurface="#F4F0FF"; choice.CustomText="#201B2C";
        Require(apply(choice) && !flag("Dark"),"custom light and dark appearance follows surface luminance");
        var light=brushes();
        Require(light[3].Color!=edited[3].Color && light[4].Color!=edited[4].Color && light[5].Color!=edited[5].Color,
                "panel, border and secondary text derive from the chosen base colors");
        choice.CustomAccent="Transparent";
        Require(apply(choice) && brushes()[2].Color==(Color)ColorConverter.ConvertFromString(UserSettings.DefaultCustomAccent),
                "an invalid live option falls back without accepting arbitrary WPF brush strings");
        Require(apply(new UserSettings{Theme="light"}) && !flag("Custom") && !flag("Dark") &&
                brushes()[0].Color!=light[0].Color,"switching from a custom light palette restores all built-in roles");
        apply(new UserSettings{Theme="system"});
    }

    static void CheckStartGlass(UserSettings settings)
    {
        using (var platform = new PlatformServices(true))
        using (var ui = new DesktopUi(platform,settings,delegate{},delegate{}))
        {
            var type = typeof(DesktopUi).Assembly.GetType("Lunaris.LaunchSurface",true);
            var pad = (Window)Activator.CreateInstance(type,InternalInstance,null,new object[]{ui},null);
            try
            {
                var glass = (Border)type.GetField("glass",InternalInstance).GetValue(pad);
                glass.Child = null; // Sample the pane between controls, including the shadow behind it.
                var sample = BitmapSource.Create(8,8,96,96,PixelFormats.Bgr32,null,new byte[8*8*4],32);
                type.GetField("backdrop",InternalInstance).SetValue(pad,sample);
                type.GetMethod("PaintGlass",InternalInstance).Invoke(pad,null);
                var surface = (FrameworkElement)pad.Content;
                surface.Measure(new Size(200,180));
                surface.Arrange(new Rect(0,0,200,180)); surface.UpdateLayout();
                var frame = new RenderTargetBitmap(200,180,96,96,PixelFormats.Pbgra32);
                frame.Render(surface);
                var pixel = new byte[4];
                frame.CopyPixels(new Int32Rect(100,90,1,1),pixel,4,0);
                Require(pixel[3] > 80 && pixel[3] < 240, "the Start pane with a refracted sample still shows the live background through its centre");
            }
            finally { pad.Close(); }
        }
    }

    static void GlassRefraction()
    {
        const int size = 32, stride = size * 4;
        byte[] source = new byte[stride * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++) {
                source[y * stride + x * 4] = (byte)(x * 8);
                source[y * stride + x * 4 + 1] = (byte)(y * 8);
            }
        var image = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgr32, null, source, stride);
        var glass = typeof(DesktopUi).Assembly.GetType("Lunaris.LaunchGlass", true);
        var result = (BitmapSource)glass.GetMethod("Refract", InternalStatic).Invoke(null, new object[] {image});
        byte[] pixels = new byte[source.Length];
        result.CopyPixels(pixels, stride, 0);
        int edge = size / 2 * stride, centre = edge + size / 2 * 4;
        Require(result.IsFrozen && pixels[edge] > source[edge] && pixels[centre] == source[centre] &&
            pixels[centre + 1] == source[centre + 1], "glass refracts a synthetic backdrop at the edge and preserves the centre");
    }

    static void MonitorRow()
    {
        var assembly=typeof(DesktopUi).Assembly;
        var screen=assembly.GetType("Lunaris.ScreenInfo",true);
        var bounds=new Rect(0,0,1920,1080);
        Func<string,bool,object> device = (id,primary) => Activator.CreateInstance(screen,InternalInstance,null,
            new object[]{id,id,primary,bounds,bounds,1.0},null);
        var a=device("preview-a",true); var b=device("preview-b",false);
        var settings = new UserSettings {Monitor="preview-b"};
        using (var platform=new PlatformServices(true))
        using (var ui=new DesktopUi(platform,settings,delegate{},delegate{}))
        {
            var setMonitors=typeof(DesktopUi).GetProperty("Monitors",InternalInstance).GetSetMethod(true);
            Action<object[]> topology = devices => {
                var array=Array.CreateInstance(screen,devices.Length);
                Array.Copy(devices,array,devices.Length);
                setMonitors.Invoke(ui,new object[]{array});
            };
            topology(new[]{a});
            var type=assembly.GetType("Lunaris.Preferences",true);
            var window=(Window)Activator.CreateInstance(type,InternalInstance,null,new object[]{ui},null);
            try
            {
                var view=(FrameworkElement)window.Content;
                var row=(Panel)view.FindName("MonitorRow"); var selector=(ComboBox)view.FindName("DockMonitor");
                var refresh=type.GetMethod("RefreshMonitors",InternalInstance);
                row.Measure(new Size(480,1000));
                Require(!window.IsVisible && row.Visibility==Visibility.Collapsed && row.DesiredSize.Height==0,
                        "real settings monitor row occupies no space for one simulated display");
                topology(new[]{a,b}); refresh.Invoke(window,null); row.Measure(new Size(480,1000));
                var values=selector.Items.Cast<ComboBoxItem>().Select(item=>(string)item.Tag).ToArray();
                Require(row.Visibility==Visibility.Visible && row.DesiredSize.Height>0 &&
                        values.SequenceEqual(new[]{"primary","preview-a","preview-b","all"}) &&
                        (string)((ComboBoxItem)selector.SelectedItem).Tag=="preview-b",
                        "real settings monitor selector appears for two displays and selects saved device");
                topology(new[]{a}); refresh.Invoke(window,null); row.Measure(new Size(480,1000));
                Require(row.Visibility==Visibility.Collapsed && row.DesiredSize.Height==0 &&
                        (string)((ComboBoxItem)selector.SelectedItem).Tag=="primary" && settings.Monitor=="preview-b",
                        "unplug hides actual monitor row and retains unavailable selection for reconnect");
            }
            finally { window.Close(); }
        }
    }

    static void Catalog()
    {
        using (var services = new PlatformServices(true))
        {
            var apps = services.GetApplications();
            Require(apps.Count > 0 && apps.All(a => !String.IsNullOrWhiteSpace(a.Id) && !String.IsNullOrWhiteSpace(a.Name)), "live shell catalog with executable identities");
            Require(apps.Select(a => a.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == apps.Count, "unique shell identities");
            Require(apps.Any(a => a.Icon != null) && apps.All(a => a.Icon == null || a.Icon.IsFrozen), "real shell icons safe to use across dispatchers");
            int packaged = apps.Count(a => a.Id.Contains("!"));
            int classic = apps.Count - packaged;
            Console.WriteLine("Catalog entries: {0}, package IDs: {1}, other IDs: {2}, icons: {3}",
                apps.Count, packaged, classic, apps.Count(a => a.Icon != null));
            Require(classic > 0 && packaged > 0, "desktop and packaged programs both discovered");
            var library=new LaunchLibrary(new UserSettings(),apps);
            var roots=new[]{Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)};
            Require(library.Folders().Where(f=>f.IsSystem).All(f=>roots.Any(r=>Directory.Exists(Path.Combine(r,f.Id.Substring(6))))),
                    "visible system folders are backed by actual directories in the Start Menu Programs trees");
            var grouped=apps.Where(a=>!String.IsNullOrEmpty(a.StartFolder)).ToList();
            Require(grouped.All(a=>library.Find(library.Find(LaunchLibrary.AppKey(a.Id)).ParentId).IsSystem),
                    "actual Programs shortcuts have browsable native parents");
            Console.WriteLine("Native Start folders: {0}, applications inside folders: {1}",library.Folders().Count(f=>f.IsSystem),grouped.Count);
            var windows = services.GetWindows();
            Require(windows.All(w => w.Handle != IntPtr.Zero && w.App != null && !String.IsNullOrEmpty(w.AppId)), "valid live window identities");
            Console.WriteLine("Visible application windows: " + windows.Count);
        }
    }

    static void StartFolderImport()
    {
        string fixture=Path.Combine(Path.GetTempPath(),"Lunaris-Programs-"+Guid.NewGuid().ToString("N"));
        string user=Path.Combine(fixture,"User"), common=Path.Combine(fixture,"Common");
        string chosen=Path.Combine(user,@"Suite\Tool.lnk"), shadow=Path.Combine(common,@"Suite\Tool.lnk");
        string other=Path.Combine(common,@"Suite\Nested\Helper.lnk");
        dynamic writer=null;
        object shell=null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(chosen));
            Directory.CreateDirectory(Path.GetDirectoryName(other));
            Directory.CreateDirectory(Path.Combine(common,@"Saved\Empty"));
            Directory.CreateDirectory(Path.Combine(user,@"HiddenEmpty\Child"));
            writer=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell",true));
            string selectedTarget=Path.Combine(Environment.SystemDirectory,"charmap.exe");
            foreach (string path in new[]{chosen,shadow,other})
            {
                dynamic shortcut=writer.CreateShortcut(path);
                try
                {
                    shortcut.TargetPath=path==chosen ? selectedTarget : Path.Combine(Environment.SystemDirectory,"cmd.exe");
                    shortcut.Arguments=path==chosen ? "" : "/?";
                    shortcut.Save();
                }
                finally { Marshal.ReleaseComObject((object)shortcut); }
            }
            byte[][] original=new[]{chosen,shadow,other}.Select(File.ReadAllBytes).ToArray();
            var type=typeof(PlatformServices).Assembly.GetType("Lunaris.ShellCatalog",true);
            var import=type.GetMethod("ImportStartFolders",InternalStatic);
            var newMap=type.GetMethod("NewMap",InternalStatic);
            shell=Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application",true));
            var intercepted=(System.Collections.IDictionary)newMap.Invoke(null,null);
            var shortcutMap=(System.Collections.IDictionary)newMap.Invoke(null,null);
            var shadowedApp=new AppEntry {Id="registered-common",Name="Other program"};
            var shadowedItem=Activator.CreateInstance(intercepted.GetType().GetGenericArguments()[1],true);
            shadowedItem.GetType().GetField("App",InternalInstance).SetValue(shadowedItem,shadowedApp);
            intercepted.Add(shadowedApp.Id,shadowedItem);
            shortcutMap.Add(shadow,shadowedItem);
            var apps=new System.Collections.Generic.List<AppEntry> {shadowedApp};
            import.Invoke(null,new object[]{shell,shortcutMap,intercepted,newMap.Invoke(null,null),apps,new[]{user,common}});
            string relative=@"startlink:Suite\Tool.lnk";
            string nested=@"startlink:Suite\Nested\Helper.lnk";
            var source=intercepted[relative];
            Require(apps.Count==3 && shadowedApp.StartFolder==null && intercepted.Contains(nested) && source!=null &&
                    String.Equals((string)source.GetType().GetField("Target",InternalInstance).GetValue(source),selectedTarget,
                                  StringComparison.OrdinalIgnoreCase),
                    "merged native trees retain the per-user shortcut and import unregistered nested common shortcuts");
            var settings=new UserSettings();
            var library=new LaunchLibrary(settings,apps);
            Require(library.Find(@"start:Suite\Nested")!=null && library.Find("start:HiddenEmpty")==null && library.Find("start:Saved")==null,
                    "physical folders with shortcuts appear with ancestors while empty native branches are hidden");
            var saved=new LibraryPlacement {Id=LaunchLibrary.AppKey(relative),Parent=@"start:Saved\Empty"};
            settings.Library.Placements.Add(saved);
            library=new LaunchLibrary(settings,apps);
            var emptyCustom=library.CreateFolder("Empty collection",LaunchLibrary.Root);
            Require(library.Find(saved.Id).ParentId==saved.Parent && library.Find("start:Saved")!=null &&
                    library.Find(emptyCustom)!=null && library.Children(emptyCustom).Count==0,
                    "existing native destinations of saved moves and empty custom folders stay accessible");
            library.Move(LaunchLibrary.AppKey(nested),emptyCustom);
            library.DeleteFolder(emptyCustom);
            var unchanged=new[]{chosen,shadow,other}.Select((path,index)=>File.ReadAllBytes(path).SequenceEqual(original[index]));
            Require(unchanged.All(match=>match),"organizing imported shortcuts preserves their exact original bytes in both trees");
        }
        finally
        {
            if (shell!=null) Marshal.ReleaseComObject(shell);
            if (writer!=null) Marshal.ReleaseComObject((object)writer);
            if (Directory.Exists(fixture)) Directory.Delete(fixture,true);
        }
    }

    static void Fonts()
    {
        Func<IEnumerable<string>,bool,string> pick = UiFonts.SelectFamilyName;
        var families = new[]{"Segoe UI","SF UI Text","SF UI Display","SF Pro","SF Pro Text","SF Pro Display"};
        Require(pick(families,false)=="SF Pro Text" && pick(families,true)=="SF Pro Display", "SF Pro Text and Display have first priority by role");
        var unified = families.Take(4).ToArray();
        Require(pick(unified,false)=="SF Pro" && pick(unified,true)=="SF Pro", "unified SF Pro is preferred over older SF UI faces");
        var legacy = families.Take(3).ToArray();
        Require(pick(legacy,false)=="SF UI Text" && pick(legacy,true)=="SF UI Display", "legacy SF UI names are selected by role");
        Require(pick(new[]{"Arial","Segoe UI"},false)=="Segoe UI" && pick(new string[0],true)=="Segoe UI", "no San Francisco installed uses Segoe UI");
        var installed = System.Windows.Media.Fonts.SystemFontFamilies
            .SelectMany(f=>f.FamilyNames.Values.Concat(new[]{f.Source})).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var alphabet = Enumerable.Range(0x410,64).Concat(new[]{0x401,0x451}).ToArray();
        foreach(string role in new[]{"Text","Display"})
        {
            var family = role=="Display" ? UiFonts.Display : UiFonts.Text;
            var expected=pick(installed,role=="Display");
            var face = new Typeface(family,FontStyles.Normal,FontWeights.Normal,FontStretches.Normal);
            GlyphTypeface glyphs;
            Require(face.TryGetGlyphTypeface(out glyphs), "physical font face resolved for " + role);
            Require(glyphs.FamilyNames.Values.Concat(glyphs.Win32FamilyNames.Values).Contains(expected,StringComparer.OrdinalIgnoreCase),
                "physical font family matches installed family priority for "+role);
            Require(alphabet.All(c=>glyphs.CharacterToGlyphMap.ContainsKey(c) && glyphs.CharacterToGlyphMap[c]!=0),
                "all 66 uppercase/lowercase Russian letters map to actual glyphs in "+role);
            Console.WriteLine("UI font {0}: selected={1}, physical={2}, file={3}",role, family.Source,
                String.Join(" / ", glyphs.FamilyNames.Values.Distinct()), glyphs.FontUri.LocalPath);
        }
    }

    static void Gestures()
    {
        var type = typeof(PlatformServices).Assembly.GetType("Lunaris.WinGesture", true);
        var input = type.GetMethod("Input", BindingFlags.NonPublic | BindingFlags.Instance);
        var state = Activator.CreateInstance(type, true);
        Func<int,bool,bool,bool> key = (vk,down,other) => (bool)input.Invoke(state, new object[]{vk,down,other});
        foreach (int win in new[]{0x5b,0x5c})
        {
            Require(!key(win,true,false) && !key(win,true,false) && key(win,false,false) && !key(win,false,false),
                    "standalone and repeated Win events 0x"+win.ToString("x"));
            foreach (int shortcut in new[]{0x45,0x52,0x44,0x4c,0x10})
            {
                bool fired=key(win,true,false) | key(shortcut,true,false) | key(shortcut,false,false) | key(win,false,false);
                Require(!fired,"chord does not open Launchpad: Win+0x"+shortcut.ToString("x"));
            }
            Require(!(key(win,true,true) | key(win,false,false)), "existing modifier suppresses standalone gesture");
        }
        Require(!(key(0x5b,true,false) | key(0x5c,true,false) | key(0x5b,false,false) | key(0x5c,false,false)), "overlapping Win keys do not toggle");
        key(0x5b,true,false);
        key(0x4c,true,false);
        type.GetMethod("Synchronize", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(state, new object[]{false,false});
        Require(!key(0x5b,true,false) && key(0x5b,false,false), "single Win recovers after a lock-screen key-up was missed");
    }

    static void TrayMenuPosition()
    {
        var method = typeof(UserSettings).Assembly.GetType("Lunaris.SteamTrayMenu")
            .GetMethod("Position", BindingFlags.NonPublic | BindingFlags.Static);
        var button = new Int32Rect(1961,1440,36,37);
        var area = new Rect(339,1478,1920,1162);
        var point = (Point)method.Invoke(null, new object[] {button, area, 237, 569, 1979d});
        Require(point == new Point(1979,1478), "Steam menu opens directly below the tray on an offset 125% monitor");
        button = new Int32Rect(-72,-1080,36,38);
        area = new Rect(-1920,-1042,1920,1042);
        point = (Point)method.Invoke(null, new object[] {button, area, 237, 569, -54d});
        Require(point == new Point(-291,-1042), "Steam menu fits to the left near the right edge of a negative-coordinate monitor");
    }

    [STAThread]
    public static int Main()
    {
        string profile = Path.Combine(Path.GetTempPath(), "Lunaris-Core-" + Guid.NewGuid().ToString("N"));
        try { Settings(profile); Library(); StartFolderImport(); Gestures(); Themes(); CustomThemes(); GlassRefraction(); MonitorRow(); TrayMenuPosition(); Fonts(); Catalog(); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            if (Directory.Exists(profile))
            {
                foreach (string file in Directory.GetFiles(profile)) File.Delete(file);
                Directory.Delete(profile);
            }
        }
    }
}
