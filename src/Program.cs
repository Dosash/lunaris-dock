using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Lunaris
{
    internal static class Program
    {
        [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);

        private static bool Has(string[] args, string option)
        {
            return Array.Exists(args, x => String.Equals(x, option, StringComparison.OrdinalIgnoreCase));
        }

        [STAThread]
        public static int Main(string[] args)
        {
            bool command = Has(args, "--exit") || Has(args, "--toggle") || Has(args, "--recover");
            try
            {
                if (args.Length > 0 && args[0] == "--guardian") return PlatformServices.RunGuardian(args);
                if (Has(args, "--recover")) { PlatformServices.RecoverTaskbar(); return 0; }
                bool preview = Has(args, "--preview");
                SettingsStore.Preview = preview;
                string identity = WindowsIdentity.GetCurrent().User.Value;
                string name = InstanceName(identity, SettingsStore.ProfileDirectory);
                using (var instance = new Mutex(false, name + ".instance"))
                {
                    bool owner;
                    try { owner = instance.WaitOne(0); }
                    catch (AbandonedMutexException) { owner = true; }
                    if (!owner) return Notify(name, Has(args, "--exit"));
                    try
                    {
                        if (command) return Has(args, "--exit") ? 0 : 2;
                        var settings = SettingsStore.Load();
                        Loc.Use(settings.Language);
                        SettingsStore.CheckPortableProfile();
                        // Two different test profiles can share a desktop only when previewing.
                        bool shellOwner;
                        using (var shell = new Mutex(true, preview ? null : @"Local\LunarisDock.Shell." + identity, out shellOwner))
                        {
                            if (!preview && !shellOwner)
                                throw new InvalidOperationException(Loc.Get("OtherProfile"));
                            return Run(name, preview, settings);
                        }
                    }
                    finally { instance.ReleaseMutex(); }
                }
            }
            catch (Exception ex)
            {
                if (!command && !Has(args, "--guardian"))
                    MessageBox.Show(Loc.Get("StartError") + "\n\n" + ex.Message,
                                    "Lunaris Dock", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }

        private static string InstanceName(string identity, string profile)
        {
            using (var sha = SHA256.Create())
            {
                string key = Path.GetFullPath(profile).TrimEnd('\\', '/').ToUpperInvariant();
                return @"Local\LunarisDock." + identity + "." +
                       BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key)), 0, 12).Replace("-", "");
            }
        }

        private static int Notify(string name, bool exit)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 5000)
            {
                try
                {
                    using (var map = MemoryMappedFile.OpenExisting(name + ".pid", MemoryMappedFileRights.Read))
                    using (var data = map.CreateViewAccessor(0, 4, MemoryMappedFileAccess.Read))
                    using (var signal = EventWaitHandle.OpenExisting(name + (exit ? ".exit" : ".toggle")))
                    using (var process = Process.GetProcessById(data.ReadInt32(0)))
                    {
                        if (!exit) AllowSetForegroundWindow(process.Id);
                        signal.Set();
                        return !exit || process.WaitForExit(15000) ? 0 : 3;
                    }
                }
                catch (FileNotFoundException) { }
                catch (WaitHandleCannotBeOpenedException) { }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                Thread.Sleep(40);
            }
            // The owner may already have exited while we were opening the signals.
            using (var check = new Mutex(false, name + ".instance"))
            {
                try { if (exit && check.WaitOne(0)) { check.ReleaseMutex(); return 0; } }
                catch (AbandonedMutexException) { check.ReleaseMutex(); if (exit) return 0; }
            }
            return 3;
        }

        private static int Run(string name, bool preview, UserSettings settings)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
            using (var toggle = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".toggle"))
            using (var quit = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".exit"))
            using (var map = MemoryMappedFile.CreateNew(name + ".pid", 4))
            using (var data = map.CreateViewAccessor())
            using (var platform = new PlatformServices(preview))
            {
                data.Write(0, Process.GetCurrentProcess().Id);
                // Setup and Preferences share this user's Run registration. An upgrade
                // can change it while the profile still holds the previous selection.
                if (!preview) settings.Startup = platform.IsStartupRegistered();
                bool replace = settings.ReplaceTaskbar;
                bool startup = settings.Startup;
                bool hideWindowsStart = settings.HideWindowsStart;
                platform.IntegrationError += delegate(string message)
                {
                    replace = settings.ReplaceTaskbar = false;
                    try { SettingsStore.Save(settings); }
                    catch (IOException ex) { message += "\n" + ex.Message; }
                    catch (UnauthorizedAccessException ex) { message += "\n" + ex.Message; }
                    MessageBox.Show(Loc.Get("TaskbarDisabled") + "\n\n" + message,
                                    "Lunaris Dock", MessageBoxButton.OK, MessageBoxImage.Warning);
                };
                Action save = delegate
                {
                    try
                    {
                        if (replace != settings.ReplaceTaskbar) platform.ApplyTaskbar(settings.ReplaceTaskbar);
                        if (startup != settings.Startup) platform.ApplyStartup(settings.Startup);
                        if (hideWindowsStart != settings.HideWindowsStart) platform.ApplyWindowsStart(settings.HideWindowsStart);
                        SettingsStore.Save(settings);
                        replace = settings.ReplaceTaskbar;
                        startup = settings.Startup;
                        hideWindowsStart = settings.HideWindowsStart;
                    }
                    catch (Exception ex)
                    {
                        string errors = ex.Message;
                        if (replace != settings.ReplaceTaskbar)
                            try { platform.ApplyTaskbar(replace); }
                            catch (Exception rollback) { errors += "\n" + Loc.Format("TaskbarIssue", rollback.Message); }
                        if (startup != settings.Startup)
                            try { platform.ApplyStartup(startup); }
                            catch (Exception rollback) { errors += "\n" + Loc.Format("StartupIssue", rollback.Message); }
                        if (hideWindowsStart != settings.HideWindowsStart)
                            try { platform.ApplyWindowsStart(hideWindowsStart); }
                            catch (Exception rollback) { errors += "\n" + Loc.Format("StartIssue", rollback.Message); }
                        settings.ReplaceTaskbar = replace;
                        settings.Startup = startup;
                        settings.HideWindowsStart = hideWindowsStart;
                        MessageBox.Show(Loc.Get("ChangesUnsaved") + "\n\n" + errors, "Lunaris Dock",
                                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                };
                var ui = new DesktopUi(platform, settings, save, () => app.Shutdown());
                app.SessionEnding += delegate { ui.Dispose(); };
                RegisteredWaitHandle toggleWait = null, quitWait = null;
                try
                {
                    platform.Start();
                    platform.ApplyTaskbar(replace);
                    ui.Start();
                    platform.ApplyWindowsStart(hideWindowsStart);
                    toggleWait = DispatchSignal(toggle, app.Dispatcher, ui.ToggleLaunchpad);
                    quitWait = DispatchSignal(quit, app.Dispatcher, ui.Exit);
                    return app.Run();
                }
                finally
                {
                    if (toggleWait != null) toggleWait.Unregister(null);
                    if (quitWait != null) quitWait.Unregister(null);
                    ui.Dispose();
                }
            }
        }

        private static RegisteredWaitHandle DispatchSignal(WaitHandle signal, Dispatcher ui, Action action)
        {
            return ThreadPool.RegisterWaitForSingleObject(signal, delegate
            {
                if (!ui.HasShutdownStarted) ui.BeginInvoke(action);
            }, null, Timeout.Infinite, false);
        }
    }
}
