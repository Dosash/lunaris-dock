using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Lunaris;

internal static class ShortcutPinTests
{
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly Assembly Product = typeof(DesktopUi).Assembly;

    static object Call(object target, string method, params object[] args)
    {
        return target.GetType().GetMethod(method, Hidden | BindingFlags.DeclaredOnly).Invoke(target, args);
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }

    static void Shortcut(string file, string target, string arguments)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
        dynamic link = null;
        try
        {
            link = shell.CreateShortcut(file);
            link.TargetPath = target;
            link.Arguments = arguments;
            link.Save();
        }
        finally
        {
            if (link != null) Marshal.ReleaseComObject((object)link);
            Marshal.ReleaseComObject((object)shell);
        }
    }

    static AppEntry RegisterStartShortcut(PlatformServices platform, string root)
    {
        object catalog = typeof(PlatformServices).GetField("catalog", Hidden).GetValue(platform);
        Type type = catalog.GetType();
        const BindingFlags helpers = BindingFlags.NonPublic | BindingFlags.Static;
        object shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true));
        try
        {
            var apps = new List<AppEntry>();
            type.GetMethod("ImportStartFolders", helpers).Invoke(null, new object[] { shell,
                type.GetMethod("NewMap", helpers).Invoke(null, null), type.GetField("items", Hidden).GetValue(catalog),
                type.GetField("targets", Hidden).GetValue(catalog), apps, new[] { root } });
            return apps.Single();
        }
        finally { Marshal.ReleaseComObject(shell); }
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLink
    {
        void GetPath(); // Vtable slots preceding SetIDList are unused by this fixture.
        void GetIDList();
        void SetIDList(IntPtr idList);
    }
    [DllImport("shell32.dll", CharSet=CharSet.Unicode)]
    static extern int SHParseDisplayName(string path, IntPtr context, out IntPtr idList, uint attributes, out uint result);

    static void PackageShortcut(PlatformServices platform, string dir)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application",true));
        dynamic folder = shell.NameSpace("shell:AppsFolder"), children = folder.Items();
        string id = null;
        try
        {
            for (int i=0; i<children.Count && id==null; ++i)
            {
                dynamic child = children.Item(i);
                try
                {
                    string path = child.Path as string;
                    if (path != null && path.Contains("!") && !Path.IsPathRooted(path)) id = path;
                }
                finally { Marshal.ReleaseComObject((object)child); }
            }
        }
        finally
        {
            Marshal.ReleaseComObject((object)children); Marshal.ReleaseComObject((object)folder); Marshal.ReleaseComObject((object)shell);
        }
        if (id == null) { Console.WriteLine("SKIP no installed AppsFolder package"); return; }
        string linkFile = Path.Combine(dir,"Store app.lnk");
        object link = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"),true));
        IntPtr pidl = IntPtr.Zero;
        try
        {
            uint attributes;
            Marshal.ThrowExceptionForHR(SHParseDisplayName("shell:AppsFolder\\"+id,IntPtr.Zero,out pidl,0,out attributes));
            ((IShellLink)link).SetIDList(pidl);
            ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Save(linkFile,true);
        }
        finally { Marshal.ReleaseComObject(link); Marshal.FreeCoTaskMem(pidl); }
        string explorerLink = Path.Combine(dir,"AppsFolder via Explorer.lnk");
        Shortcut(explorerLink,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe"),"shell:AppsFolder\\"+id);
        foreach (var shortcut in new[]{linkFile,explorerLink})
        {
            WithoutWriteAccess(shortcut, delegate {
                var pins = (string[])Call(platform,"ResolveDrop",(object)new[]{shortcut});
                Require(pins.SequenceEqual(new[]{id}), Path.GetFileName(shortcut) + " resolves to its installed Store app without write access");
            });
        }
    }

    static DragDropEffects Drag(FrameworkElement source, string[] paths, DragDropEffects allowed, RoutedEvent action, double x)
    {
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, paths);
        return Drag(source, data, allowed, action, x);
    }

    static DragDropEffects Drag(FrameworkElement source, IDataObject data, DragDropEffects allowed, RoutedEvent action, double x)
    {
        var args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), Hidden, null,
            new object[] { data, DragDropKeyStates.None, allowed, source, new Point(x, 2) }, null);
        args.RoutedEvent = action;
        source.RaiseEvent(args);
        return args.Effects;
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out System.Runtime.InteropServices.ComTypes.IDataObject data);
    }

    [DllImport("shell32.dll", CharSet=CharSet.Unicode, PreserveSig=false)]
    static extern void SHCreateItemFromParsingName(string name, IntPtr context, ref Guid iid, out IShellItem item);

    static void DropShellItem(StackPanel strip, string file, string exe)
    {
        Guid itemId = typeof(IShellItem).GUID, dataId = typeof(System.Runtime.InteropServices.ComTypes.IDataObject).GUID;
        Guid handler = new Guid("B8C0BD9F-ED24-455C-83E6-D5390C4FE8C4"); // BHID_DataObject
        IShellItem item;
        SHCreateItemFromParsingName(file, IntPtr.Zero, ref itemId, out item);
        System.Runtime.InteropServices.ComTypes.IDataObject native = null;
        try
        {
            item.BindToHandler(IntPtr.Zero, ref handler, ref dataId, out native);
            var data = new DataObject(native);
            var files = (string[])data.GetData(DataFormats.FileDrop, false);
            Require(files != null && files.SequenceEqual(new[]{file}, StringComparer.OrdinalIgnoreCase),
                "Windows Shell supplies native FileDrop for " + Path.GetFileName(file));
            var allowed = DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link;
            var enter = Drag(strip,data,allowed,DragDrop.PreviewDragEnterEvent,0);
            var drop = Drag(strip,data,allowed,DragDrop.PreviewDropEvent,0);
            Console.WriteLine("Shell drag effects for " + Path.GetFileName(file) + ": enter=" + enter + ", drop=" + drop);
            Require(enter == DragDropEffects.Link && drop == DragDropEffects.Link &&
                SettingsStore.Load().Pins.Contains(exe, StringComparer.OrdinalIgnoreCase) && File.Exists(file),
                "read-only shell shortcut pins its executable without modifying the original: " + Path.GetFileName(file));
        }
        finally
        {
            if (native != null) Marshal.ReleaseComObject(native);
            Marshal.ReleaseComObject(item);
        }
    }

    static void WithoutWriteAccess(string file, Action test)
    {
        var original = File.GetAccessControl(file);
        var restricted = File.GetAccessControl(file);
        using (var user = WindowsIdentity.GetCurrent())
            restricted.AddAccessRule(new FileSystemAccessRule(user.User, FileSystemRights.Write, AccessControlType.Deny));
        File.SetAccessControl(file, restricted);
        try { test(); }
        finally { File.SetAccessControl(file,original); }
    }

    static void ReadOnlyShellDrops(StackPanel strip, string dir)
    {
        string exe = Path.Combine(Environment.SystemDirectory,"where.exe");
        string file = Path.Combine(dir, "Read-only desktop shortcut.lnk");
        Shortcut(file,exe,"");
        WithoutWriteAccess(file, delegate { DropShellItem(strip,file,exe); });

        string shared = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Steam.lnk");
        if (!File.Exists(shared)) return;
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
        dynamic link = null;
        try
        {
            link = shell.CreateShortcut(shared);
            string target = link.TargetPath;
            if (File.Exists(target) && String.Equals(Path.GetExtension(target),".exe",StringComparison.OrdinalIgnoreCase))
                DropShellItem(strip,shared,target);
        }
        finally
        {
            if (link != null) Marshal.ReleaseComObject((object)link);
            Marshal.ReleaseComObject((object)shell);
        }
    }

    static void Test(string dir)
    {
        string programs = Path.Combine(dir,"Programs"), exe = Path.Combine(dir,"Drop Probe.exe");
        string alias = Path.Combine(programs,"Renamed shortcut.lnk"), desktop = Path.Combine(dir,"Desktop shortcut.lnk");
        string broken = Path.Combine(dir,"Broken.lnk"), document = Path.Combine(dir,"Notes.txt");
        string before = Path.Combine(Environment.SystemDirectory,"charmap.exe"), after = Path.Combine(Environment.SystemDirectory,"cmd.exe");
        string marker = Path.Combine(dir,"started.txt");
        Directory.CreateDirectory(programs);
        Environment.SetEnvironmentVariable("LUNARIS_PROFILE", Path.Combine(dir, "Profile"));
        File.Copy(Assembly.GetExecutingAssembly().Location, exe);
        File.Copy(Product.Location, Path.Combine(dir, "LunarisDock.exe"));
        Shortcut(alias, exe, "");
        Shortcut(desktop, exe, "--not-part-of-the-executable");
        Shortcut(broken, Path.Combine(dir,"missing.exe"), "");
        File.WriteAllText(document, "");
        using (var platform = new PlatformServices(true))
        {
            PackageShortcut(platform,dir);
            var registered = RegisterStartShortcut(platform, programs);
            var settings = new UserSettings { Pins = new List<string> { before, registered.Id, after } };
            using (var ui = new DesktopUi(platform, settings, () => SettingsStore.Save(settings), delegate {}))
            {
                var bounds = new Rect(-31000, -31000, 1000, 800);
                var screen = Activator.CreateInstance(Product.GetType("Lunaris.ScreenInfo"), Hidden, null,
                    new object[] { "fixture","Fixture",true,bounds,bounds,1.0 }, null);
                var dock = (Window)Activator.CreateInstance(Product.GetType("Lunaris.DockSurface"), Hidden, null, new object[]{ui,screen},null);
                try
                {
                    Call(dock,"Render",screen);
                    dock.ShowActivated = false;
                    dock.Show(); dock.UpdateLayout();
                    var strip = (StackPanel)dock.GetType().GetField("strip",Hidden).GetValue(dock);
                    // Save the normal fixture's profile after exercising real Shell selection data.
                    var originalPins = settings.Pins.ToList();
                    ReadOnlyShellDrops(strip, dir);
                    string folder = Directory.CreateDirectory(Path.Combine(dir, "Documents QA")).FullName;
                    string folderLink = Path.Combine(dir, "Documents shortcut.lnk");
                    Shortcut(folderLink, folder, "");
                    var beforeFolderDrop = settings.Pins.ToList();
                    Drag(strip,new[]{folderLink,folder + Path.DirectorySeparatorChar,folder},DragDropEffects.Link,DragDrop.PreviewDropEvent,0);
                    string directoryPin = SettingsStore.Load().Pins.Except(beforeFolderDrop).Single();
                    var directory = (AppEntry)Call(ui,"FindApp",directoryPin);
                    Require(directory != null && directory.Name == "Documents QA" &&
                        (string)typeof(AppEntry).GetField("DirectoryPath",Hidden).GetValue(directory) == folder &&
                        settings.Pins.Count(id => id == directoryPin) == 1,
                        "dropping a directory and its shortcut creates one persistent Explorer folder pin");
                    Directory.Delete(folder);
                    File.WriteAllText(folder, "");
                    bool unavailable = false;
                    try { platform.Launch(directory); } catch (DirectoryNotFoundException) { unavailable = true; }
                    Require(unavailable && Call(ui,"FindApp",directoryPin) != null,
                        "a missing directory stays removable and a replacement file cannot be launched");
                    settings.Pins = originalPins;
                    SettingsStore.Save(settings);
                    Call(dock,"Render",screen); dock.UpdateLayout();
                    string[] paths = { desktop, exe, broken, document };
                    Require(Drag(strip,paths,DragDropEffects.Copy | DragDropEffects.Link,DragDrop.PreviewDragOverEvent,0) == DragDropEffects.Link &&
                        settings.Pins[1] == registered.Id && !File.Exists(marker), "Explorer hover accepts links without executing or pinning");
                    Require(Drag(strip,paths,DragDropEffects.Move,DragDrop.PreviewDropEvent,0) == DragDropEffects.None &&
                        Drag(strip,new[]{broken,document},DragDropEffects.Copy,DragDrop.PreviewDropEvent,0) == DragDropEffects.None,
                        "move-only, unsupported and missing shortcut targets are ignored");
                    Require(Drag(strip,paths,DragDropEffects.Copy,DragDrop.PreviewDropEvent,0) == DragDropEffects.Copy &&
                        SettingsStore.Load().Pins.SequenceEqual(new[]{exe,before,after}) && !File.Exists(marker) && File.Exists(desktop),
                        "drop inserts the target EXE before the first pin and folds duplicate inputs and its old Start identity");
                    Call(dock,"Render",screen); dock.UpdateLayout();
                    Drag(strip,new[]{desktop},DragDropEffects.Link,DragDrop.PreviewDropEvent,strip.ActualWidth);
                    Require(SettingsStore.Load().Pins.SequenceEqual(new[]{before,after,exe}) &&
                        (bool)Call(ui,"Pinned",registered.Id), "repeated drop reorders the same pin and the registered app is marked pinned");
                    File.Delete(alias); File.Delete(desktop);
                    string pin = SettingsStore.Load().Pins.Last();
                    Call(ui,"CanonicalizePins");
                    var app = (AppEntry)Call(ui,"FindApp",pin);
                    Require(settings.Pins.Last() == exe && app.Id == registered.Id, "catalog identity groups the pin without replacing its stored path");
                    platform.Launch(app);
                }
                finally { dock.Close(); }
            }
        }
        WaitForProbe(marker, "EXE launches after both shortcuts are deleted even with the stale shell PIDL in the catalog");
        File.Delete(marker);
        using (var platform = new PlatformServices(true))
        using (var ui = new DesktopUi(platform,SettingsStore.Load(),delegate{},delegate{}))
        {
            var pin = SettingsStore.Load().Pins.Last();
            platform.Launch((AppEntry)Call(ui,"FindApp",pin));
            WaitForProbe(marker, "fresh catalog launches the persisted executable without the original shortcut");
        }
    }

    static void WaitForProbe(string marker, string message)
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(marker) && timer.ElapsedMilliseconds < 5000) Thread.Sleep(40);
        Require(File.Exists(marker), message);
        int pid = Int32.Parse(File.ReadAllText(marker));
        try
        {
            using (var probe = Process.GetProcessById(pid))
                if (!probe.WaitForExit(5000)) throw new Exception("Probe failed to exit.");
        }
        catch (ArgumentException) { /* Already exited. */ }
    }

    [STAThread]
    public static int Main()
    {
        if (Path.GetFileName(Assembly.GetExecutingAssembly().Location) == "Drop Probe.exe")
        {
            string marker = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "started.txt");
            File.WriteAllText(marker + ".tmp", Process.GetCurrentProcess().Id.ToString());
            File.Move(marker + ".tmp", marker);
            return 0;
        }
        var dir = Path.Combine(Path.GetTempPath(),"Lunaris-Dropped-Pin-" + Guid.NewGuid().ToString("N"));
        try { Test(dir); return 0; }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
