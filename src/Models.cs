using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Windows.Media;
using Microsoft.Win32.SafeHandles;

namespace Lunaris
{
    public sealed class AppEntry
    {
        public string Id, Name;
        // A pin imported from Explorer launches this executable even when Id is a registered app.
        internal string DirectPath;
        public ImageSource Icon;
        public string StartFolder;
        internal Dictionary<string,string> StartFolderNames;
    }

    public sealed class WindowEntry
    {
        public string AppId, Title;
        internal string ProcessPath;
        public IntPtr Handle;
        public AppEntry App;
    }

    [DataContract]
    public sealed class UserSettings
    {
        public const string DefaultCustomSurface = "#27283D";
        public const string DefaultCustomText = "#F5F2FF";
        public const string DefaultCustomAccent = "#BAA7FF";

        [DataMember] public List<string> Pins = new List<string>();
        [DataMember] public double Size = 48;
        [DataMember] public bool Magnify = true;
        [DataMember] public bool Startup;
        [DataMember] public bool ReplaceTaskbar;
        [DataMember] public bool HideDockWhenCovered = true;
        [DataMember] public bool HideWindowsStart;
        [DataMember] public bool InitializePins = true;
        [DataMember] public string Theme = "system";
        [DataMember] public string Language = Loc.DefaultLanguage;
        [DataMember] public bool LiquidGlass;
        [DataMember] public bool ShowUpdateBadge = true;
        [DataMember] public string CustomSurface = DefaultCustomSurface;
        [DataMember] public string CustomText = DefaultCustomText;
        [DataMember] public string CustomAccent = DefaultCustomAccent;
        [DataMember] public string Monitor = "primary";
        [DataMember] public LibrarySettings Library = new LibrarySettings();

        [OnDeserializing]
        private void Reading(StreamingContext context)
        {
            Size = 48;
            Magnify = true;
            HideDockWhenCovered = true;
            ShowUpdateBadge = true;
            Theme = "system";
            Language = Loc.DefaultLanguage;
            CustomSurface = DefaultCustomSurface;
            CustomText = DefaultCustomText;
            CustomAccent = DefaultCustomAccent;
            Monitor = "primary";
            Library = new LibrarySettings();
        }

        internal void Normalize()
        {
            if (double.IsNaN(Size) || double.IsInfinity(Size)) Size = 48;
            Size = Math.Max(28, Math.Min(80, Size));
            Theme = NormalizeTheme(Theme);
            Language = Loc.Normalize(Language);
            CustomSurface = NormalizeColor(CustomSurface, DefaultCustomSurface);
            CustomText = NormalizeColor(CustomText, DefaultCustomText);
            CustomAccent = NormalizeColor(CustomAccent, DefaultCustomAccent);
            if (String.IsNullOrWhiteSpace(Monitor)) Monitor = "primary";
            if (Library == null) Library = new LibrarySettings();
            Library.Normalize();
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Pins = (Pins ?? new List<string>()).FindAll(id => !String.IsNullOrWhiteSpace(id) && unique.Add(id));
        }

        internal static string NormalizeTheme(string mode)
        {
            mode = mode == null ? "" : mode.Trim();
            if (String.Equals(mode,"light",StringComparison.OrdinalIgnoreCase)) return "light";
            if (String.Equals(mode,"dark",StringComparison.OrdinalIgnoreCase)) return "dark";
            if (String.Equals(mode,"custom",StringComparison.OrdinalIgnoreCase)) return "custom";
            return "system";
        }

        internal static string NormalizeColor(string value, string fallback)
        {
            if (value == null) return fallback;
            value = value.Trim();
            if (value.Length != 7 || value[0] != '#') return fallback;
            bool lowercase = false;
            for (int i = 1; i < value.Length; ++i)
            {
                char c = value[i];
                if (c >= '0' && c <= '9' || c >= 'A' && c <= 'F') continue;
                if (c < 'a' || c > 'f') return fallback;
                lowercase = true;
            }
            return lowercase ? value.ToUpperInvariant() : value;
        }
    }

    public static class SettingsStore
    {
        internal static bool Preview { private get; set; }

        internal static bool Portable
        {
            get { return String.Equals(ConfigurationManager.AppSettings["LunarisPortable"], "true", StringComparison.OrdinalIgnoreCase); }
        }

        public static string ProfileDirectory
        {
            get
            {
                var path = Environment.GetEnvironmentVariable("LUNARIS_PROFILE");
                if (String.IsNullOrWhiteSpace(path))
                {
                    string folder = Preview ? "Preview" : "Profile";
                    return Portable
                        ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", folder)
                        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LunarisDock", folder);
                }
                string root = Path.GetPathRoot(path);
                // D:folder and \folder are relative to process drive state on Windows.
                if (String.IsNullOrEmpty(root) || !(root.StartsWith(@"\\", StringComparison.Ordinal) ||
                    (root.Length == 3 && root[1] == ':')))
                    throw new ArgumentException(Loc.Get("ProfilePathError"));
                return Path.GetFullPath(path);
            }
        }

        internal static void CheckPortableProfile()
        {
            if (!Portable) return;
            string directory = ProfileDirectory;
            try
            {
                Directory.CreateDirectory(directory);
                string probe = Path.Combine(directory, ".lunaris-" + Guid.NewGuid().ToString("N") + ".tmp");
                using (var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                                 4096, FileOptions.DeleteOnClose))
                {
                    file.WriteByte(0);
                    file.Flush(true);
                }
                CheckReplaceableFile(Path.Combine(directory, "settings.json"));
                CheckReplaceableFile(Path.Combine(directory, "settings.json.bak"));
            }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)) throw;
                throw new IOException(Loc.Format("PortableWriteError", directory), ex);
            }
        }

        private static void CheckReplaceableFile(string path)
        {
            const uint readWriteDelete = 0x80000000u | 0x40000000u | 0x00010000u;
            // OPEN_EXISTING never creates or truncates the user's file. Request DELETE as
            // well as write access: another reader may allow writes but forbid replacement.
            using (var existing = CreateFile(path, readWriteDelete, FileShare.ReadWrite | FileShare.Delete,
                                              IntPtr.Zero, FileMode.Open, 0, IntPtr.Zero))
            {
                if (!existing.IsInvalid) return;
                int error = Marshal.GetLastWin32Error();
                if (error == 2) return; // No old file to replace.
                throw new IOException(Loc.Format("UpdateFileError", Path.GetFileName(path)),
                                      new Win32Exception(error));
            }
        }

        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share,
            IntPtr security, FileMode mode, uint flags, IntPtr template);

        public static UserSettings Load()
        {
            var path = Path.Combine(ProfileDirectory, "settings.json");
            Exception readError = null;
            foreach (var candidate in new[] { path, path + ".bak" })
            {
                if (!File.Exists(candidate)) continue;
                try
                {
                    using (var file = File.OpenRead(candidate))
                    {
                        var result = (UserSettings)new DataContractJsonSerializer(typeof(UserSettings)).ReadObject(file);
                        if (result == null) continue;
                        result.Normalize();
                        return result;
                    }
                }
                catch (SerializationException) { }
                catch (System.Xml.XmlException) { }
                catch (IOException ex) { readError = ex; }
                catch (UnauthorizedAccessException ex) { readError = ex; }
            }
            if (readError != null) throw new IOException(Loc.Get("ReadSettingsError"), readError);
            return new UserSettings();
        }

        public static void Save(UserSettings value)
        {
            value.Normalize();
            Directory.CreateDirectory(ProfileDirectory);
            var path = Path.Combine(ProfileDirectory, "settings.json");
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    new DataContractJsonSerializer(typeof(UserSettings)).WriteObject(file, value);
                    file.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
                else File.Move(temp, path);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
    }
}
