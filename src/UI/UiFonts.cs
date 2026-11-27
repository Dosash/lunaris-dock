using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace Lunaris
{
    public static class UiFonts
    {
        private const string Fallback = "Segoe UI";
        private static readonly string[] TextOrder = {
            "SF Pro Text", "SF Pro", "SF UI Text", "San Francisco Text", "San Francisco",
            ".SF NS Text", "SF Pro Display", "SF UI Display", "San Francisco Display", ".SF NS Display"
        };
        private static readonly string[] DisplayOrder = {
            "SF Pro Display", "SF Pro", "SF UI Display", "San Francisco Display", "San Francisco",
            ".SF NS Display", "SF Pro Text", "SF UI Text", "San Francisco Text", ".SF NS Text"
        };

        public static string SelectFamilyName(IEnumerable<string> installedFamilies, bool display)
        {
            var names = new HashSet<string>(installedFamilies, StringComparer.OrdinalIgnoreCase);
            foreach (string name in display ? DisplayOrder : TextOrder)
                if (names.Contains(name)) return name;
            return Fallback;
        }

        public static FontFamily Text { get { return Installed.Text; } }
        public static FontFamily Display { get { return Installed.Display; } }

        private static class Installed
        {
            internal static readonly FontFamily Text, Display;

            static Installed()
            {
                var available = new Dictionary<string, FontFamily>(StringComparer.OrdinalIgnoreCase);
                foreach (var family in Fonts.SystemFontFamilies)
                {
                    available[family.Source] = family;
                    foreach (string localized in family.FamilyNames.Values)
                        if (!available.ContainsKey(localized)) available.Add(localized, family);
                }
                Text = Resolve(available, false);
                Display = Resolve(available, true);
            }

            private static FontFamily Resolve(Dictionary<string, FontFamily> available, bool display)
            {
                FontFamily family;
                return available.TryGetValue(SelectFamilyName(available.Keys, display), out family) ?
                    family : new FontFamily(Fallback);
            }
        }
    }
}
