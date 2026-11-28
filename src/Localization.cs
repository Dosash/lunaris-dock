using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace Lunaris
{
    internal static class Loc
    {
        internal static readonly string[] Codes = { "en", "ru", "de", "es", "zh-CN" };
        internal static readonly string[] Names = { "English", "Русский", "Deutsch", "Español", "简体中文" };
        internal static readonly string DefaultLanguage = DetectDefault();

        // Replacing an immutable catalogue also updates messages raised on background threads.
        private sealed class Catalog
        {
            internal readonly string Code;
            internal readonly Dictionary<string,string> Strings = new Dictionary<string,string>(StringComparer.Ordinal);

            internal Catalog(string code)
            {
                Code = code;
                using (var stream = typeof(Loc).Assembly.GetManifestResourceStream("Lunaris.Languages." + code + ".txt"))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (String.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal)) continue;
                        int separator = line.IndexOf('=');
                        Strings.Add(line.Substring(0, separator), line.Substring(separator + 1));
                    }
                }
            }
        }

        private static volatile Catalog active = new Catalog(DefaultLanguage);
        internal static string Language { get { return active.Code; } }
        internal static IEnumerable<KeyValuePair<string,string>> Strings { get { return active.Strings; } }

        private static string Supported(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return null;
            value = value.Trim();
            if (String.Equals(value, "zhCN", StringComparison.OrdinalIgnoreCase)) return "zh-CN";
            foreach (string code in Codes)
                if (String.Equals(code, value, StringComparison.OrdinalIgnoreCase)) return code;
            // Regional variants use their language's translation; Chinese uses simplified script.
            try
            {
                var culture = CultureInfo.GetCultureInfo(value);
                string primary = culture.TwoLetterISOLanguageName;
                if (primary == "zh") return "zh-CN";
                if (Array.IndexOf(Codes, primary) >= 0) return primary;
            }
            catch (CultureNotFoundException) { }
            return null;
        }

        private static string DetectDefault()
        {
            if (!SettingsStore.Portable)
            {
                try
                {
                    var installed = Registry.GetValue(@"HKEY_CURRENT_USER\Software\LunarisDock", "InstallLanguage", null) as string;
                    string code = Supported(installed);
                    if (code != null) return code;
                }
                catch (System.Security.SecurityException) { }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            return Supported(CultureInfo.CurrentUICulture.Name) ?? "en";
        }

        internal static string Normalize(string code) { return Supported(code) ?? DefaultLanguage; }

        internal static void Use(string code)
        {
            code = Normalize(code);
            if (active.Code != code) active = new Catalog(code);
        }

        internal static string Get(string key) { return active.Strings[key]; }
        internal static string Format(string key, params object[] values)
        {
            var catalog = active;
            return String.Format(CultureInfo.GetCultureInfo(catalog.Code), catalog.Strings[key], values);
        }
    }
}
