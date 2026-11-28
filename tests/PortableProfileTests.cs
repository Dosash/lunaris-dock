using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Windows.Automation;
using Lunaris;
using AE = System.Windows.Automation.AutomationElement;

// Read the application settings in a domain whose config and ApplicationBase match
// the extracted executable. This exercises ordinary mode without touching Explorer.
public sealed class PortableDomainProbe : MarshalByRefObject
{
    public string[] ReadPaths(string exe)
    {
        var store = Assembly.LoadFrom(exe).GetType("Lunaris.SettingsStore", true);
        var preview = store.GetProperty("Preview", BindingFlags.Static | BindingFlags.NonPublic);
        var path = store.GetProperty("ProfileDirectory");
        preview.SetValue(null, false, null);
        string normal = (string)path.GetValue(null, null);
        preview.SetValue(null, true, null);
        return new[] { normal, (string)path.GetValue(null, null) };
    }
}

internal static class PortableProfileTests
{
    static string exe, cwd, profile, proof;
    const string PortableFolder="Lunaris Portable QA";

    static void Require(bool value, string label)
    {
        if (!value) throw new Exception(label);
        Console.WriteLine("PASS " + label);
    }

    static string[] Paths(string config)
    {
        var setup = new AppDomainSetup { ApplicationBase = Path.GetDirectoryName(exe), ConfigurationFile = config };
        var domain = AppDomain.CreateDomain("Lunaris path probe", null, setup);
        try {
            var remote = (PortableDomainProbe)domain.CreateInstanceFromAndUnwrap(
                typeof(PortableDomainProbe).Assembly.Location, typeof(PortableDomainProbe).FullName);
            return remote.ReadPaths(exe);
        }
        finally { AppDomain.Unload(domain); }
    }

    static void CheckPaths()
    {
        var paths = Paths(exe + ".config");
        var root = Path.Combine(Path.GetDirectoryName(exe), "Data");
        Require(paths[0] == Path.Combine(root, "Profile") && paths[1] == Path.Combine(root, "Preview"),
            "portable Profile and Preview resolve beside the extracted EXE from a different working directory");
        var installed = Paths(Path.Combine(cwd, "installed.config"));
        var installedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LunarisDock");
        Require(installed[0] == Path.Combine(installedRoot, "Profile") && installed[1] == Path.Combine(installedRoot, "Preview"),
            "default installed profiles are distinct from portable profiles");
        Require(!Directory.Exists(Path.Combine(cwd, "Data")), "working directory has no portable data");
    }

    static UserSettings Read()
    {
        using (var file = File.OpenRead(profile))
            return (UserSettings)new DataContractJsonSerializer(typeof(UserSettings)).ReadObject(file);
    }

    static T Wait<T>(Process process, Func<T> probe, string label) where T : class
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < 35000)
        {
            if (process.HasExited) throw new Exception("Dock exited early: " + process.ExitCode + " (" + label + ")");
            try { T value = probe(); if (value != null) return value; }
            catch (ElementNotAvailableException) { }
            catch (IOException) { }
            Thread.Sleep(130);
        }
        throw new TimeoutException(label);
    }

    static AE Window(Process p, string id)
    {
        var roots = AE.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AE.ProcessIdProperty, p.Id));
        return roots.Cast<AE>().FirstOrDefault(w => w.Current.AutomationId == id && !w.Current.IsOffscreen);
    }

    static AE[] Buttons(Process p)
    {
        var bar = Window(p, "Dock");
        return bar == null ? new AE[0] : bar.FindAll(TreeScope.Descendants,
            new PropertyCondition(AE.AutomationIdProperty, "Dock.App")).Cast<AE>().ToArray();
    }

    static Process Start(string args)
    {
        return Process.Start(new ProcessStartInfo(exe, args) { WorkingDirectory = cwd, UseShellExecute = false });
    }

    static void Command(string command)
    {
        using (var c = Start("--preview " + command))
            if (!c.WaitForExit(20000) || c.ExitCode != 0) throw new Exception("Preview command failed: " + command);
    }

    static void Stop(Process p)
    {
        Command("--exit");
        Require(p.WaitForExit(5000) && p.ExitCode == 0, "--preview --exit ended the exact portable process");
    }

    static void RunPreview(bool first)
    {
        using (var dock = Start("--preview"))
        {
            try {
                if (first) {
                    var saved = Wait(dock, delegate {
                        if (!File.Exists(profile)) return null;
                        var s = Read(); return !s.InitializePins && s.Pins.Count >= 2 ? s : null;
                    }, "first-run portable settings and at least two registered applications");
                    var buttons = Wait(dock, delegate {
                        var list = Buttons(dock);
                        return list.Length >= 2 && list[0].Current.BoundingRectangle.Width > 0 ? list : null;
                    }, "visible portable pins");
                    string[] names={buttons[1].Current.Name,buttons[0].Current.Name};
                    File.WriteAllLines(proof, new[] { names[0], names[1],
                        buttons[0].Current.BoundingRectangle.Width.ToString("R", CultureInfo.InvariantCulture),
                        saved.Pins[1], saved.Pins[0] });
                    Require(File.Exists(profile), "first preview wrote Data/Preview/settings.json beside the ZIP executable");
                    Stop(dock);
                    // A known two-pin order differs from first-run defaults. PreviewTests
                    // separately drives pinning, drag-and-drop and settings through the GUI.
                    saved.Pins = saved.Pins.Take(2).Reverse().ToList();
                    saved.Size = 70; saved.Magnify = false;
                    saved.HideDockWhenCovered=false; saved.HideWindowsStart=true;
                    var apps=saved.Pins.Select((id,i)=>new AppEntry {Id=id,Name=names[i]}).ToList();
                    var library=new LaunchLibrary(saved,apps);
                    string folder=library.CreateFolder(PortableFolder,LaunchLibrary.Root);
                    library.SetManualOrder(LaunchLibrary.Root);
                    foreach(var app in apps)
                        if(!library.Move(LaunchLibrary.AppKey(app.Id),folder)) throw new Exception("Cannot seed portable folder.");
                    using (var file = File.Create(profile))
                        new DataContractJsonSerializer(typeof(UserSettings)).WriteObject(file, saved);
                }
                else {
                    string[] expected = File.ReadAllLines(proof);
                    double originalWidth = Double.Parse(expected[2], CultureInfo.InvariantCulture);
                    Wait(dock, delegate {
                        var list = Buttons(dock);
                        return list.Length >= 2 && list[0].Current.Name == expected[0] && list[1].Current.Name == expected[1] &&
                            list[0].Current.BoundingRectangle.Width > originalWidth ? list : null;
                    }, "relocated Dock must render the saved size and reordered pins");
                    Console.WriteLine("PASS relocated Dock renders the saved size and reordered pins");
                    var saved = Read();
                    Require(saved.Size == 70 && !saved.Magnify && !saved.HideDockWhenCovered && saved.HideWindowsStart &&
                        saved.Pins.SequenceEqual(expected.Skip(3)),
                        "relocated preview kept all saved preferences and pin identities");
                    Command("--toggle");
                    var pad=Wait(dock, () => Window(dock, "Launchpad"), "relocated IPC opens Launchpad");
                    var folder=Wait(dock,()=>pad.FindAll(TreeScope.Descendants,new PropertyCondition(AE.AutomationIdProperty,"Launchpad.Folder"))
                        .Cast<AE>().FirstOrDefault(e=>e.Current.Name==PortableFolder),"relocated Launchpad displays the saved folder");
                    ((InvokePattern)folder.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                    Wait(dock,delegate {
                        var names=pad.FindAll(TreeScope.Descendants,new PropertyCondition(AE.AutomationIdProperty,"Launchpad.App"))
                            .Cast<AE>().Select(e=>e.Current.Name).ToArray();
                        return names.SequenceEqual(expected.Take(2)) ? names : null;
                    },"relocated folder contains both apps in the original manual order");
                    Console.WriteLine("PASS relocated folder and app order render from the moved portable profile");
                    Command("--toggle");
                    Wait(dock, () => Window(dock, "Launchpad")==null ? Window(dock, "Dock") : null, "relocated IPC closes Launchpad");
                    Console.WriteLine("PASS --preview --toggle opens and closes the relocated Launchpad");
                    Stop(dock);
                }
            }
            finally {
                if (!dock.HasExited) {
                    try { Command("--exit"); } catch (Exception) { }
                    if (!dock.WaitForExit(5000)) { dock.Kill(); dock.WaitForExit(); }
                }
            }
        }
    }

    [STAThread] static int Main(string[] args)
    {
        try {
            exe = Path.GetFullPath(args[1]); cwd = Path.GetFullPath(args[2]); proof = Path.GetFullPath(args[3]);
            Directory.SetCurrentDirectory(cwd);
            Environment.SetEnvironmentVariable("LUNARIS_PROFILE", null);
            profile = Path.Combine(Path.GetDirectoryName(exe), "Data", "Preview", "settings.json");
            switch (args[0]) {
                case "paths": CheckPaths(); break;
                case "prepare": RunPreview(true); break;
                case "moved": RunPreview(false); break;
                default: throw new ArgumentException("Unexpected mode");
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
