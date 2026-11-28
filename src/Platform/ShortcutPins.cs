using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Lunaris
{
    internal sealed partial class ShellCatalog
    {
        internal string PinIdentity(string pin)
        {
            if (!IsExecutable(pin)) return pin;
            lock (gate)
            {
                Item registered;
                return targets.TryGetValue(pin, out registered) ? registered.App.Id : pin;
            }
        }

        internal string PinTarget(string id)
        {
            if (IsExecutable(id)) return id;
            lock (gate)
            {
                Item item;
                return items.TryGetValue(id, out item) && IsExecutable(item.Target) ? item.Target : id;
            }
        }

        internal AppEntry ResolveExecutablePin(string path)
        {
            if (!File.Exists(path)) return null;
            var app = Match(null, null, path);
            return new AppEntry { Id = app.Id, Name = app.Name, Icon = app.Icon, DirectPath = path,
                StartFolder = app.StartFolder, StartFolderNames = app.StartFolderNames };
        }

        internal string[] ResolveDrop(string[] files)
        {
            var pins = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            object shell = null;
            try
            {
                foreach (string file in files)
                {
                    try
                    {
                        if (!AbsolutePath(file) || !File.Exists(file)) continue;
                        string pin = null;
                        if (IsExecutable(file)) pin = Path.GetFullPath(file);
                        else if (file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                        {
                            if (shell == null) shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true));
                            pin = ReadShortcut((dynamic)shell, Path.GetFullPath(file));
                        }
                        if (pin != null && seen.Add(pin)) pins.Add(pin);
                    }
                    catch (COMException) { }
                    catch (IOException) { }
                    catch (ArgumentException) { }
                    catch (NotSupportedException) { }
                    catch (UnauthorizedAccessException) { }
                    catch (System.Security.SecurityException) { }
                }
            }
            finally { Release(shell); }
            return pins.ToArray();
        }

        private static bool AbsolutePath(string path)
        {
            return !String.IsNullOrEmpty(path) && (path.StartsWith(@"\\", StringComparison.Ordinal) ||
                path.Length >= 3 && path[1] == ':' && (path[2] == '\\' || path[2] == '/'));
        }

        private string ReadShortcut(dynamic shell, string file)
        {
            dynamic folder = null, item = null;
            try
            {
                folder = shell.NameSpace(Path.GetDirectoryName(file));
                if (folder == null) return null;
                item = folder.ParseName(Path.GetFileName(file));
                if (item == null || !item.IsLink) return null;
                // FolderItem.GetLink requests write access even when only reading Path.
                // Read shell properties directly so all-users desktop shortcuts work.
                string target = LinkProperty(item, "System.Link.TargetParsingPath");
                if (String.IsNullOrWhiteSpace(target)) target = ReadLinkPath(file);
                if (!String.IsNullOrWhiteSpace(target))
                {
                    target = Environment.ExpandEnvironmentVariables(target);
                    if (AbsolutePath(target) && IsExecutable(target))
                    {
                        if (!File.Exists(target)) return null;
                        // Explorer can itself be a launcher for an AppsFolder item.
                        if (String.Equals(target, Path.Combine(Environment.GetFolderPath(
                            Environment.SpecialFolder.Windows), "explorer.exe"), StringComparison.OrdinalIgnoreCase))
                        {
                            string arguments = (LinkProperty(item, "System.Link.Arguments") ?? "").Trim().Trim('"');
                            if (arguments.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase))
                                return InstalledPackage(shell, arguments);
                        }
                        return Path.GetFullPath(target);
                    }
                    if (AbsolutePath(target)) return null;
                }
                return InstalledPackage(shell, target) ?? InstalledPackage(shell, LinkProperty(item, "System.AppUserModel.ID"));
            }
            finally { Release((object)item); Release((object)folder); }
        }

        private static string LinkProperty(dynamic item, string name)
        {
            try { return item.ExtendedProperty(name) as string; }
            catch (COMException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path,
                                      int capacity, IntPtr fileData, uint flags);
        }

        private static string ReadLinkPath(string file)
        {
            object link = null;
            try
            {
                link = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), true));
                ((IPersistFile)link).Load(file, 0x40); // STGM_READ | STGM_SHARE_DENY_NONE
                var path = new StringBuilder(32768);
                return ((IShellLinkW)link).GetPath(path, path.Capacity, IntPtr.Zero, 4) == 0 ? path.ToString() : null; // SLGP_RAWPATH
            }
            catch (COMException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            finally { Release(link); }
        }

        private string InstalledPackage(dynamic shell, string target)
        {
            if (String.IsNullOrWhiteSpace(target)) return null;
            const string folder = "shell:AppsFolder\\";
            const string guid = "::{4234d49b-0245-4df3-b780-3893943456e1}\\";
            if (target.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) target = target.Substring(folder.Length);
            else if (target.StartsWith(guid, StringComparison.OrdinalIgnoreCase)) target = target.Substring(guid.Length);
            if (!target.Contains("!")) return null;
            lock (gate)
            {
                Item item;
                if (items.TryGetValue(target, out item)) return item.App.Id;
            }
            // The user may drop a shortcut before the background catalog scan finishes.
            dynamic apps = null, app = null;
            try
            {
                apps = shell.NameSpace("shell:AppsFolder");
                if (apps == null) return null;
                app = apps.ParseName(target);
                return app != null && String.Equals((string)app.Path, target, StringComparison.OrdinalIgnoreCase)
                    ? target : null;
            }
            finally { Release((object)app); Release((object)apps); }
        }
    }
}
