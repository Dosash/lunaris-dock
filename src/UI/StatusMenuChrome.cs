using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Lunaris
{
    internal static class StatusMenuChrome
    {
        private static readonly Lazy<ResourceDictionary> styles = new Lazy<ResourceDictionary>(() => {
            using (var stream = typeof(StatusMenuChrome).Assembly.GetManifestResourceStream("Lunaris.StatusMenuStyles.xaml"))
                return (ResourceDictionary)XamlReader.Load(stream);
        });

        internal static void Apply(Window window, Border frame)
        {
            if (!window.Resources.MergedDictionaries.Contains(styles.Value))
                window.Resources.MergedDictionaries.Add(styles.Value);
            bool dark = Appearance.Dark, custom = Appearance.Custom, hc = SystemParameters.HighContrast;
            var r = window.Resources;
            var glass = Appearance.Glass;
            frame.Background = hc ? SystemColors.WindowBrush : glass != null ? glass.Pane :
                custom ? Appearance.Surface : Visuals.Brush(dark ? "#27282D" : "#F7F7F9");
            frame.BorderBrush = hc ? SystemColors.WindowTextBrush : glass != null ? glass.Rim : Appearance.Border;
            r["MenuText"] = hc ? SystemColors.WindowTextBrush : Appearance.Text;
            r["MenuMuted"] = hc ? SystemColors.WindowTextBrush : Appearance.Muted;
            r["MenuPanel"] = hc ? SystemColors.WindowBrush : Appearance.Panel;
            r["MenuBorder"] = hc ? SystemColors.WindowTextBrush : Appearance.Border;
            r["MenuAccent"] = hc ? SystemColors.HighlightBrush : custom ? Appearance.Accent : Visuals.Brush(dark ? "#8AC8FF" : "#0965C0");
            r["MenuActive"] = hc ? SystemColors.HighlightBrush : custom ? Appearance.Accent : Visuals.Brush(dark ? "#368BE8" : "#0874DA");
            r["MenuHover"] = hc ? SystemColors.ControlBrush : custom ? Appearance.Selection : Visuals.Brush(dark ? "#45464D" : "#E8E9ED");
            r["MenuSelection"] = hc ? SystemColors.ControlBrush : custom ? Appearance.Selection : Visuals.Brush(dark ? "#30475F" : "#DFECFA");
            r["MenuRail"] = hc ? SystemColors.GrayTextBrush : custom ? Appearance.Border : Visuals.Brush(dark ? "#51535A" : "#DADCDF");
            r["MenuSwitchOff"] = hc ? SystemColors.GrayTextBrush : custom ? Appearance.Muted : Visuals.Brush(dark ? "#63666F" : "#969CA4");
            r["MenuKnob"] = hc ? SystemColors.WindowBrush : Brushes.White;
            r["MenuKnobEdge"] = hc ? SystemColors.WindowTextBrush : Visuals.Brush("#26000000");
            r["MenuOnline"] = hc ? SystemColors.WindowTextBrush : Visuals.Brush(dark ? "#70C993" : "#278050");
            window.Foreground = (Brush)r["MenuText"];
            r[SystemColors.ControlTextBrushKey] = window.Foreground;
            r[SystemColors.HighlightBrushKey] = r["MenuActive"];
        }

        internal static void StyleSwitch(CheckBox box)
        {
            box.SetResourceReference(FrameworkElement.StyleProperty, "MenuSwitch");
            box.Loaded += delegate { MoveSwitch(box, false); };
            RoutedEventHandler changed = delegate { if (box.IsLoaded) MoveSwitch(box, Appearance.Motion); };
            box.Checked += changed;
            box.Unchecked += changed;
            box.Indeterminate += changed;
        }

        private static void MoveSwitch(CheckBox box, bool animate)
        {
            var thumb = box.Template == null ? null : box.Template.FindName("thumb", box) as Border;
            if (thumb == null) return;
            var shift = thumb.RenderTransform as TranslateTransform;
            if (shift == null) thumb.RenderTransform = shift = new TranslateTransform();
            double from = shift.X, to = box.IsChecked == true ? 14 : 0;
            shift.BeginAnimation(TranslateTransform.XProperty, null);
            shift.X = to;
            if (animate && from != to)
                shift.BeginAnimation(TranslateTransform.XProperty, Motion(from, to, 135));
        }

        internal static DoubleAnimation Motion(double from, double to, int ms)
        {
            return new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms)) {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
        }

        internal static TextBlock Glyph(string glyph, double size, string ink)
        {
            var mark = new TextBlock { Text = glyph, FontSize = size, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
            mark.SetResourceReference(TextBlock.ForegroundProperty, ink);
            return mark;
        }

        internal static TextBlock Label(string value, double size, string ink)
        {
            var text = new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            text.SetResourceReference(TextBlock.ForegroundProperty, ink);
            return text;
        }

        internal static Border Rule()
        {
            var line = new Border { Height = 1, Opacity = .65, Margin = new Thickness(3,8,3,8) };
            line.SetResourceReference(Border.BackgroundProperty, "MenuBorder");
            return line;
        }
    }
}
