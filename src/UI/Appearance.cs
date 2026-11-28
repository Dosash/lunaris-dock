using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Lunaris
{
    internal static class Appearance
    {
        internal static bool Dark { get; private set; }
        internal static bool Custom { get; private set; }
        internal static bool Motion { get { return SystemParameters.ClientAreaAnimation; } }
        internal static SolidColorBrush Text { get; private set; }
        internal static SolidColorBrush Muted { get; private set; }
        internal static SolidColorBrush Surface { get; private set; }
        internal static SolidColorBrush Panel { get; private set; }
        internal static SolidColorBrush Border { get; private set; }
        internal static SolidColorBrush Accent { get; private set; }
        internal static SolidColorBrush Selection { get; private set; }
        internal static LiquidGlassMaterial Glass { get; private set; }
        private static readonly Stopwatch sinceRead = Stopwatch.StartNew();
        private static string lastMode;
        private static string lastSurface, lastText, lastAccent;
        private static bool systemDark;

        static Appearance() { Refresh("system"); }

        internal static bool Refresh(string mode)
        {
            return RefreshAppearance(mode,UserSettings.DefaultCustomSurface,UserSettings.DefaultCustomText,UserSettings.DefaultCustomAccent,false);
        }

        internal static bool RefreshFor(UserSettings settings)
        {
            if (settings == null) return Refresh("system");
            return RefreshAppearance(settings.Theme,settings.CustomSurface,settings.CustomText,settings.CustomAccent,settings.LiquidGlass);
        }

        private static bool RefreshAppearance(string mode, string surface, string text, string accent, bool liquidGlass)
        {
            bool paletteChanged = RefreshPalette(mode, surface, text, accent);
            bool enabled = liquidGlass && LaunchGlass.Enabled();
            if (!paletteChanged && enabled == (Glass != null)) return false;
            Glass = enabled ? new LiquidGlassMaterial(Surface.Color, Panel.Color, Accent.Color, Dark) : null;
            return true;
        }

        private static bool RefreshPalette(string mode,string surface,string text,string accent)
        {
            mode = UserSettings.NormalizeTheme(mode);
            if (mode == "system" && (lastMode != mode || sinceRead.ElapsedMilliseconds >= 650))
            {
                try
                {
                    object value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                                                     "AppsUseLightTheme", 1);
                    systemDark = value is int && (int)value == 0;
                }
                catch (UnauthorizedAccessException) { systemDark = false; }
                catch (System.Security.SecurityException) { systemDark = false; }
                sinceRead.Restart();
            }
            lastMode = mode;
            if (mode == "custom")
            {
                surface = UserSettings.NormalizeColor(surface,UserSettings.DefaultCustomSurface);
                text = UserSettings.NormalizeColor(text,UserSettings.DefaultCustomText);
                accent = UserSettings.NormalizeColor(accent,UserSettings.DefaultCustomAccent);
                if (Custom && surface == lastSurface && text == lastText && accent == lastAccent) return false;
                lastSurface = surface; lastText = text; lastAccent = accent;
                Custom = true;
                Surface = Visuals.Brush(surface);
                Text = Visuals.Brush(text);
                Accent = Visuals.Brush(accent);
                // WPF supplies linearized sRGB channels. At 0.179, white and black
                // foregrounds have approximately equal contrast against the surface.
                Color color = Surface.Color;
                Dark = .2126 * color.ScR + .7152 * color.ScG + .0722 * color.ScB < .179;
                Panel = Mix(color,Colors.White,Dark ? .07 : .55);
                Border = Mix(color,Text.Color,Dark ? .27 : .23);
                Muted = Mix(color,Text.Color,.74);
                Selection = Mix(Panel.Color,Accent.Color,Dark ? .27 : .20);
                return true;
            }
            bool dark = mode == "dark" || (mode == "system" && systemDark);
            if (!Custom && Text != null && dark == Dark) return false;
            Custom = false;
            Dark = dark;
            Text      = Tone("#202630", "#F5F6F9");
            Muted     = Tone("#526174", "#BCC6D2");
            Surface   = Tone("#EFF3F8", "#222730");
            Panel     = Tone("#FCFDFE", "#303642");
            Border    = Tone("#CFD7E0", "#4D5663");
            Accent    = Tone("#1467B8", "#83C3FF");
            Selection = Tone("#D7EAFD", "#354E69");
            return true;
        }

        private static SolidColorBrush Tone(string light, string dark)
        {
            return Visuals.Brush(Dark ? dark : light);
        }

        private static SolidColorBrush Mix(Color background,Color foreground,double fraction)
        {
            var brush = new SolidColorBrush(Color.FromRgb(
                (byte)Math.Round(background.R + (foreground.R - background.R) * fraction),
                (byte)Math.Round(background.G + (foreground.G - background.G) * fraction),
                (byte)Math.Round(background.B + (foreground.B - background.B) * fraction)));
            brush.Freeze();
            return brush;
        }
    }
}
