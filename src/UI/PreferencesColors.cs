using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace Lunaris
{
    internal sealed partial class Preferences
    {
        private RadioButton customChoice;

        private void BuildColorEditor()
        {
            AddColorField("CustomSurface", Loc.Get("SurfaceColor"), () => ui.Settings.CustomSurface, value => ui.Settings.CustomSurface = value);
            AddColorField("CustomText", Loc.Get("TextColor"), () => ui.Settings.CustomText, value => ui.Settings.CustomText = value);
            AddColorField("CustomAccent", Loc.Get("AccentColor"), () => ui.Settings.CustomAccent, value => ui.Settings.CustomAccent = value);
        }

        private void AddColorField(string id, string name, Func<string> getColor, Action<string> setColor)
        {
            var column = new StackPanel { Margin = new Thickness(3, 0, 3, 2) };
            var label = new TextBlock { Text = name, FontSize = 12, Margin = new Thickness(2, 0, 0, 4) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            column.Children.Add(label);
            var frame = new Border { CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1) };
            frame.SetResourceReference(Border.BackgroundProperty, "Surface");
            frame.SetResourceReference(Border.BorderBrushProperty, "Rule");
            var row = new DockPanel { Height = 30 };
            var pick = Visuals.Button("Choose" + id, Loc.Format("PickColor", name));
            pick.Width = 28;
            var swatch = new Border { Width = 17, Height = 17, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Background = Visuals.Brush(getColor()) };
            swatch.SetResourceReference(Border.BorderBrushProperty, "Rule");
            pick.Content = swatch;
            DockPanel.SetDock(pick,Dock.Left);
            row.Children.Add(pick);
            var field = new TextBox { Text = getColor(), FontFamily = UiFonts.Text, FontSize = 12.5, Background = Brushes.Transparent,
                Padding = new Thickness(0,0,3,0), BorderThickness = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center,
                MaxLength = 7, ToolTip = Loc.Get("ColorFormat") };
            Visuals.Identify(field, id, Loc.Format("ColorCode", name));
            field.SetResourceReference(TextBox.ForegroundProperty, "Ink");
            field.SetResourceReference(TextBox.CaretBrushProperty, "Ink");
            field.TextChanged += delegate {
                string input = field.Text.Trim();
                if (input.Length == 6 && !input.StartsWith("#", StringComparison.Ordinal)) input = "#" + input;
                string hex = UserSettings.NormalizeColor(input, null);
                if (hex == null || hex == getColor()) return;
                setColor(hex);
                swatch.Background = Visuals.Brush(hex);
                ui.Changed();
            };
            field.LostKeyboardFocus += delegate { field.Text = getColor(); };
            field.PreviewKeyDown += delegate(object sender, KeyEventArgs e) {
                if (e.Key != Key.Enter) return;
                field.Text = getColor(); field.SelectAll(); e.Handled = true;
            };
            pick.Click += delegate {
                var current = Visuals.Brush(getColor()).Color;
                using (var chooser = new Forms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B) })
                {
                    var owner = new ColorDialogOwner(new WindowInteropHelper(this).Handle);
                    if (chooser.ShowDialog(owner) != Forms.DialogResult.OK) return;
                    var color = chooser.Color;
                    field.Text = String.Format("#{0:X2}{1:X2}{2:X2}",color.R,color.G,color.B);
                }
            };
            row.Children.Add(field);
            frame.Child = row;
            column.Children.Add(frame);
            Part<Panel>("ColorFields").Children.Add(column);
        }

        private sealed class ColorDialogOwner : Forms.IWin32Window
        {
            public IntPtr Handle { get; private set; }
            internal ColorDialogOwner(IntPtr handle) { Handle = handle; }
        }

        private void UpdateColorEditor()
        {
            var fields = Part<Panel>("CustomColors");
            var visibility = ui.Settings.Theme == "custom" ? Visibility.Visible : Visibility.Collapsed;
            bool layoutChanged = visibility != fields.Visibility;
            fields.Visibility = visibility;
            customChoice.Content = ThemeExample("custom", Loc.Get("Theme.custom"));
            if (!layoutChanged || !IsVisible) return;
            // A newly visible row inside a ScrollViewer can retain its parent's old desired height.
            for (UIElement element = fields; element != null; element = VisualTreeHelper.GetParent(element) as UIElement)
            {
                element.InvalidateMeasure();
                if (element == view) break;
            }
            // Let the radio event and the new row's layout finish before resizing the HWND.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(delegate {
                if (!IsVisible || dismissing || fields.Visibility != visibility) return;
                var center = PointToScreen(new Point(ActualWidth / 2,ActualHeight / 2));
                var screen = ui.Monitors.FirstOrDefault(s => s.Contains(center)) ?? ui.ActiveMonitor;
                if (screen != null) SizeToScreen(screen,PointToScreen(new Point(0,0)));
                UpdateLayout();
            }));
        }

        private void PaintCustom(DrawingContext dc)
        {
            var surface = Visuals.Brush(ui.Settings.CustomSurface);
            var text = Visuals.Brush(ui.Settings.CustomText);
            var accent = Visuals.Brush(ui.Settings.CustomAccent);
            dc.DrawRectangle(surface,null,new Rect(0,0,124,54));
            dc.PushOpacity(.48);
            dc.DrawGeometry(accent,null,Geometry.Parse("M0,39 Q25,7 60,31 Q95,49 124,12 L124,54 L0,54 Z"));
            dc.Pop();
            dc.DrawRoundedRectangle(surface,new Pen(accent,1.4),new Rect(34,8,57,32),5,5);
            dc.DrawRoundedRectangle(text,null,new Rect(40,14,24,2),1,1);
            for (int i = 0; i < 4; i++)
                dc.DrawRoundedRectangle(i == 1 ? text : accent,null,new Rect(40+i*12,22,8,9),2,2);
            dc.DrawRoundedRectangle(accent,null,new Rect(43,46,38,5),2.5,2.5);
        }
    }
}
