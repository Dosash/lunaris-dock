using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Controls.Primitives;

namespace Lunaris
{
    internal sealed class DockSurface : Window
    {
        private readonly DesktopUi ui;
        private readonly StackPanel strip = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly Border plate;
        private readonly TranslateTransform slide = new TranslateTransform();
        private readonly List<PinSlot> slots = new List<PinSlot>();
        private readonly DockItemMotion items;
        private int previousPinCount;
        private bool themeChanged;
        private ScreenInfo screen;
        private double cell, art;
        private AnimationClock slideClock;
        private SlideCurve curve;
        private static PinDrag drag;
        private IDataObject externalData;
        private string[] externalPins;
        private bool revealed;
        private ContextMenu shownMenu;
        private const string PinData = "Lunaris.Dock.App.v1";
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int w, int h, uint flags);
        internal bool Busy { get { return drag != null || (shownMenu != null && shownMenu.IsOpen); } }
        internal bool WantsPointer { get { return Busy || (revealed && IsMouseOver); } }
        internal bool IsRevealed { get { return revealed; } }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        private static readonly uint OwnPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

        internal void KeepAboveForeground()
        {
            var front = GetForegroundWindow();
            uint pid;
            if (front == IntPtr.Zero || GetWindowThreadProcessId(front, out pid) == 0 || pid == OwnPid ||
                MonitorFromWindow(front, 2) != screen.Handle) return;
            var hwnd = new WindowInteropHelper(this).Handle;
            var visited = new HashSet<IntPtr>();
            for (IntPtr higher = GetWindow(hwnd, 3); higher != IntPtr.Zero && visited.Add(higher); higher = GetWindow(higher, 3))
            {
                if (higher != front) continue;
                SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
                break;
            }
        }

        internal DockSurface(DesktopUi ui, ScreenInfo screen)
        {
            this.ui = ui; this.screen = screen;
            items = new DockItemMotion(strip);
            Closed += delegate {
                items.Cancel();
                slideClock = null;
                slide.ApplyAnimationClock(TranslateTransform.YProperty, null);
            };
            Title = "Lunaris Dock";
            FontFamily = UiFonts.Text;
            Visuals.Identify(this, "Dock", Title);
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = 0; Top = 0; Width = 1; Height = 1;
            ShowInTaskbar = false; ShowActivated = false; Topmost = true;
            AllowsTransparency = true; Background = Brushes.Transparent;
            UseLayoutRounding = true; SnapsToDevicePixels = true;
            SourceInitialized += delegate { Visuals.ToolWindow(this, true); };

            plate = new Border {
                BorderThickness = new Thickness(1), RenderTransform = slide,
                CornerRadius = new CornerRadius(15), HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(9, 0, 9, 5),
                Padding = new Thickness(5, 2, 5, 2), Effect = Visuals.Shadow(10, 3, .45), Child = strip };
            // Layered windows hit-test their alpha channel. Keep the edge truly transparent
            // even when the soft shadow would leave one almost invisible pixel there.
            Content = new Border { Child = plate, ClipToBounds = true, Margin = new Thickness(0, 0, 0, 2) };
            ApplyTheme();
            AttachMenu(plate, FillMainMenu);
            plate.AllowDrop = true;
            plate.PreviewDragEnter += TrackDrop;
            plate.PreviewDragOver += TrackDrop;
            plate.PreviewDrop += FinishDrop;
            plate.DragLeave += delegate(object sender, DragEventArgs e) {
                if (!new Rect(plate.RenderSize).Contains(e.GetPosition(plate))) ClearExternalDrop();
                if (drag != null && drag.Surface == this && !new Rect(plate.RenderSize).Contains(e.GetPosition(plate)))
                    RestoreOrder();
            };
        }

        internal void ApplyTheme()
        {
            themeChanged = true;
            var material = Appearance.Glass;
            if (material != null)
            {
                plate.Background = material.Dock;
                plate.BorderBrush = material.Rim;
                return;
            }
            Color top=Visuals.Mix(Appearance.Surface.Color,Appearance.Panel.Color,.68);
            Color bottom=Visuals.Mix(Appearance.Surface.Color,Appearance.Border.Color,.16);
            top.A=245; bottom.A=236;
            plate.Background = new LinearGradientBrush(top,bottom,90);
            plate.BorderBrush = Appearance.Border;
        }

        // Hermite interpolation keeps position and velocity continuous when the pointer
        // reverses a slide. Both endpoints are at rest on an uninterrupted journey.
        private sealed class SlideCurve : IEasingFunction
        {
            internal readonly double From,To,Millis;
            private readonly double speed,limit;
            internal static SlideCurve Create(double from,double to,double initialSpeed,double travel,bool motion)
            {
                return motion && Math.Abs(to-from)>.35 ? new SlideCurve(from,to,initialSpeed,travel) : null;
            }
            private SlideCurve(double from,double to,double initialSpeed,double travel)
            {
                From=from; To=to; speed=initialSpeed; limit=travel;
                Millis=105+(to==0 ? 195 : 160)*Math.Sqrt(Math.Min(1,Math.Abs(to-from)/travel));
            }
            private double Position(double u)
            {
                return From+(To-From)*u*u*(3-2*u) + speed*Millis*u*(1-u)*(1-u);
            }
            public double Ease(double u)
            {
                double position=Math.Max(0,Math.Min(limit,Position(u)));
                return (position-From)/(To-From);
            }
            internal double Speed(double u)
            {
                double value=Position(u);
                if(value<0 || value>limit) return 0;
                return (To-From)*6*u*(1-u)/Millis + speed*(1-u)*(1-3*u);
            }
        }

        internal void SetRevealed(bool show, bool animate)
        {
            bool allowMotion=animate && Appearance.Motion;
            if (!show || !Appearance.Motion) items.Finish();
            if (show==revealed && (slideClock==null ? IsVisible==show : allowMotion)) return;
            revealed = show;
            double hidden = Math.Max(Height, 1) + 4;
            double from=IsVisible ? slide.Y : hidden;
            double speed=0;
            if(slideClock!=null) {
                double position=slideClock.CurrentProgress.GetValueOrDefault();
                if(!slideClock.CurrentProgress.HasValue) from=curve.From;
                speed=curve.Speed(position);
                slideClock=null;
                slide.ApplyAnimationClock(TranslateTransform.YProperty,null);
            }
            from=Math.Min(hidden,Math.Max(0,from));
            slide.Y=from;
            if (show && !IsVisible) Show();
            if (show)
                SetWindowPos(new WindowInteropHelper(this).Handle,new IntPtr(-1),0,0,0,0,
                    0x0001 | 0x0002 | 0x0010); // HWND_TOPMOST, NOSIZE | NOMOVE | NOACTIVATE.
            double to = show ? 0 : hidden;
            slide.Y = to;
            curve=SlideCurve.Create(from,to,speed,hidden,allowMotion);
            if (curve==null) { if (!show && IsVisible) Hide(); return; }
            var animation=new DoubleAnimation(from,to,TimeSpan.FromMilliseconds(curve.Millis)) {
                EasingFunction=curve, FillBehavior=FillBehavior.HoldEnd
            };
            AnimationClock clock=animation.CreateClock();
            slideClock=clock;
            clock.Completed+=delegate {
                if(slideClock!=clock) return;
                slideClock=null;
                slide.ApplyAnimationClock(TranslateTransform.YProperty,null);
                if(!revealed && IsVisible) Hide();
            };
            slide.ApplyAnimationClock(TranslateTransform.YProperty,clock);
            // Install the sampled starting position before Show's first render pass.
            clock.Controller.Begin();
            clock.Controller.SeekAlignedToLastTick(TimeSpan.Zero,TimeSeekOrigin.BeginTime);
        }

        internal void Render(ScreenInfo screen)
        {
            Rect bounds = ui.Settings.ReplaceTaskbar ? screen.Bounds : screen.WorkArea;
            double nextArt = Math.Max(24, Math.Min(85, ui.Settings.Size)) * .85;
            bool animate = IsVisible && revealed && Appearance.Motion && !themeChanged && art == nextArt && this.screen.SameLayout(screen);
            var before = items.Capture(animate);
            double oldWidth = Width * screen.Scale;
            themeChanged = false;
            this.screen = screen;
            strip.Children.Clear();
            slots.Clear();
            art = nextArt;
            cell = Math.Ceiling(art * 1.18 + 4);
            var pinned = ui.Settings.Pins.Select(ui.FindApp).Where(a => a != null).ToList();
            var used = new HashSet<string>(pinned.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
            var running = ui.Windows.Where(w => !String.IsNullOrWhiteSpace(w.AppId)).Select(w => ui.FindApp(w.AppId))
                .Where(a => a != null && used.Add(a.Id)).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase);
            var apps = pinned.Concat(running).ToList();
            int limit = Math.Max(1, (int)((bounds.Width / screen.Scale - 65) / cell) - 1);
            bool overflow = apps.Count > limit;
            int visibleCount = overflow ? Math.Max(0, limit - 1) : apps.Count;
            MakeButton(null);
            if (apps.Count > 0) Separator("start-line");
            for (int i = 0; i < visibleCount; ++i)
            {
                if (i == pinned.Count && i > 0) Separator("running-line");
                MakeButton(apps[i]);
            }
            if (overflow)
            {
                var more = Visuals.Button("Dock.More", Loc.Get("MoreApps"));
                more.Tag = "overflow";
                more.Width = cell;
                var glyph = Visuals.Label("•••", 16, "#E4E6F1"); glyph.Foreground = Appearance.Text;
                glyph.HorizontalAlignment = HorizontalAlignment.Center;
                more.Content = glyph;
                more.Click += delegate {
                    var menu = Visuals.Menu();
                    foreach (var entry in apps.Skip(visibleCount))
                    {
                        var outer = new MenuItem { Header = entry.Name, FontFamily = UiFonts.Text };
                        FillAppMenu(outer, entry);
                        menu.Items.Add(outer);
                    }
                    Popup(more, menu);
                };
                strip.Children.Add(more);
            }
            plate.Measure(new Size(Double.PositiveInfinity, Double.PositiveInfinity));
            double width = Math.Min(bounds.Width, Math.Ceiling((plate.DesiredSize.Width + 2) * screen.Scale));
            double height = Math.Min(bounds.Height, Math.Ceiling((cell + 23) * screen.Scale));
            Action<double> place = w => screen.Place(this, new Rect(Math.Round(bounds.Left + (bounds.Width-w)/2), bounds.Bottom-height, w, height));
            // Allow departing icons to finish inside the old HWND. Transparent margins
            // on either side of the centred plate still pass clicks to the desktop.
            bool playing = items.Play(before, previousPinCount != pinned.Count, delegate { place(width); });
            previousPinCount = pinned.Count;
            place(playing ? Math.Min(bounds.Width, Math.Max(oldWidth, width)) : width);
        }

        private void Separator(string key)
        {
            strip.Children.Add(new Border { Tag = key, Width = 1, Height = art * .74, Background = Appearance.Muted, Opacity = .42,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) });
        }

        private void MakeButton(AppEntry app)
        {
            bool first = app == null;
            var button = Visuals.Button(first ? "OpenLaunchpad" : "Dock.App", first ? Loc.Get("Start") : app.Name);
            button.Tag = first ? "start" : "app:" + app.Id;
            button.Focusable = false;
            button.Width = cell; button.Height = cell;
            var canvas = new Grid();
            var scale = new ScaleTransform(1, 1);
            var raise = new TranslateTransform();
            var group = new TransformGroup(); group.Children.Add(scale); group.Children.Add(raise);
            var image = new Border { Child = first ? Visuals.LaunchIcon(art) : Visuals.Icon(app, art),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 2), RenderTransform = group, RenderTransformOrigin = new Point(.5, .9) };
            if (first && ui.Settings.ShowUpdateBadge && ui.AvailableUpdate != null)
            {
                var artwork = image.Child;
                var marked = new Grid();
                image.Child = marked;
                marked.Children.Add(artwork);
                double diameter = Math.Max(8, Math.Round(art * .24));
                var badge = new Ellipse { Width = diameter, Height = diameter, HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0,-1,-1,0), IsHitTestVisible = false,
                    Fill = SystemParameters.HighContrast ? SystemColors.HighlightBrush : Visuals.Brush("#DD493E"),
                    Stroke = SystemParameters.HighContrast ? SystemColors.WindowBrush : Brushes.White, StrokeThickness = 1.5 };
                AutomationProperties.SetAutomationId(badge, "Dock.UpdateBadge");
                marked.Children.Add(badge);
                string status = Loc.Format("UpdateStatus", ui.AvailableUpdate.Version);
                AutomationProperties.SetItemStatus(button, status);
                AutomationProperties.SetHelpText(button, status);
                button.ToolTip = Visuals.Tip(Loc.Get("Start") + "\n" + status);
            }
            canvas.Children.Add(image);
            if (!first && ui.WindowsFor(app.Id).Count > 0)
                canvas.Children.Add(new Ellipse { Height = 3, Width = 3, Fill = Appearance.Text,
                    VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center });
            button.Content = canvas;
            button.MouseEnter += delegate {
                if (ui.Settings.Magnify) AnimateIcon(scale, raise, 1.20, -3);
            };
            button.MouseLeave += delegate { AnimateIcon(scale, raise, 1, 0); };
            if (first)
            {
                button.Click += delegate { ui.ToggleLaunchpad(screen); };
                AttachMenu(button, FillMainMenu);
            }
            else
            {
                button.Click += delegate {
                    var windows = ui.WindowsFor(app.Id);
                    if (windows.Count == 0) ui.Launch(app);
                    else if (windows.Count == 1) ui.Activate(windows[0]);
                    else {
                        var menu = Visuals.Menu();
                        FillWindows(menu, windows);
                        Popup(button, menu);
                    }
                };
                AttachMenu(button, delegate(ContextMenu m) { FillAppMenu(m, app); });
                EnableDrag(button, app);
            }
            strip.Children.Add(button);
        }

        private void AnimateIcon(ScaleTransform scale, TranslateTransform raise, double factor, double y)
        {
            if (drag != null) { factor = 1; y = 0; }
            if (Appearance.Motion) {
                Visuals.Animate(scale, ScaleTransform.ScaleXProperty, factor, 130);
                Visuals.Animate(scale, ScaleTransform.ScaleYProperty, factor, 130);
                Visuals.Animate(raise, TranslateTransform.YProperty, y, 130);
            } else {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty,null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty,null);
                raise.BeginAnimation(TranslateTransform.YProperty,null);
                scale.ScaleX = scale.ScaleY = factor; raise.Y = y;
            }
        }

        private sealed class PinSlot
        {
            internal Button Button;
            internal string Id;
            internal TranslateTransform Offset = new TranslateTransform();
            internal double Goal;
            internal double Left { get { return LayoutInformation.GetLayoutSlot(Button).Left; } }
        }

        private sealed class PinDrag
        {
            internal string Id, Target;
            internal bool After;
            internal double Grip;
            internal DockSurface Surface;
            internal DockSurface[] Docks;
        }

        private static DoubleAnimation Ease(double from, double to, int ms)
        {
            return new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms)) {
                FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        }

        private static void Shift(PinSlot slot, double x, bool animate)
        {
            if (Math.Abs(x-slot.Goal) < .05) return;
            double previous = slot.Offset.X;
            slot.Goal = x;
            slot.Offset.BeginAnimation(TranslateTransform.XProperty, null);
            slot.Offset.X = x;
            if (animate && Appearance.Motion)
                slot.Offset.BeginAnimation(TranslateTransform.XProperty, Ease(previous, x, 155));
        }

        private void EnableDrag(Button button, AppEntry app)
        {
            var slot = new PinSlot { Button = button, Id = ui.PinKey(app.Id) ?? app.Id };
            button.AllowDrop = true;
            button.RenderTransform = slot.Offset;
            slots.Add(slot);
            Point down = new Point();
            bool armed = false;
            button.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) {
                down = e.GetPosition(button); armed = true;
            };
            button.PreviewMouseLeftButtonUp += delegate { armed = false; };
            button.PreviewMouseMove += delegate(object sender, MouseEventArgs e) {
                if (!armed || drag != null || e.LeftButton != MouseButtonState.Pressed) return;
                var p = e.GetPosition(button);
                if (Math.Abs(p.X-down.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(p.Y-down.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                armed = false; e.Handled = true;
                foreach (var dock in Application.Current.Windows.OfType<DockSurface>().Where(d => d.ui == ui))
                    dock.items.Finish();
                drag = new PinDrag { Id = slot.Id, Grip = down.X / button.ActualWidth,
                    Docks = Application.Current.Windows.OfType<DockSurface>().Where(d => d.ui == ui).ToArray() };
                Mouse.Capture(null);
                try { DragDrop.DoDragDrop(button, new DataObject(PinData, slot.Id), DragDropEffects.Move); }
                finally {
                    if (drag != null) { RestoreOrder(); drag = null; }
                }
            };
        }

        private static void RestoreOrder()
        {
            drag.Surface = null;
            foreach (var dock in drag.Docks)
                foreach (var slot in dock.slots)
                {
                    Shift(slot, 0, true);
                    slot.Offset.Y = 0;
                    Panel.SetZIndex(slot.Button, 0);
                }
        }

        private void TrackDrop(object sender, DragEventArgs e)
        {
            if (drag == null) { ExternalDrop(e, false); return; }
            if (!e.Data.GetDataPresent(PinData)) return;
            double pointer = e.GetPosition(strip).X;
            bool after;
            string target = DropAnchor(pointer, drag.Id, out after);
            bool changed = drag.Surface != this || drag.After != after || !DesktopUi.IdEqual(drag.Target,target);
            drag.Target = target; drag.After = after; drag.Surface = this;
            if (changed)
            {
                var order = ProposedPins();
                foreach (var dock in drag.Docks) dock.PreviewOrder(order);
            }
            var picked = slots.FirstOrDefault(s => DesktopUi.IdEqual(s.Id,drag.Id));
            if (picked != null) {
                double x = Math.Max(0, Math.Min(strip.ActualWidth-cell,pointer-cell*drag.Grip));
                Shift(picked,x-picked.Left,false);
            }
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }

        private string DropAnchor(double pointer, string exclude, out bool after)
        {
            // Use layout slots, independent of any presentation animation.
            string last = null;
            foreach (var slot in slots)
            {
                if (!ui.Pinned(slot.Id) || DesktopUi.IdEqual(slot.Id, exclude)) continue;
                if (pointer < slot.Left + cell / 2) { after = false; return slot.Id; }
                last = slot.Id;
            }
            after = true;
            return last;
        }

        private void ClearExternalDrop() { externalData = null; externalPins = null; }

        private void ExternalDrop(DragEventArgs e, bool commit)
        {
            e.Handled = true;
            e.Effects = DragDropEffects.None;
            // Reporting Move would allow Explorer to remove the original file.
            var effect = (e.AllowedEffects & DragDropEffects.Link) != 0 ? DragDropEffects.Link :
                (e.AllowedEffects & DragDropEffects.Copy);
            try
            {
                if (effect == DragDropEffects.None || !e.Data.GetDataPresent(DataFormats.FileDrop, false)) return;
                if (commit || !Object.ReferenceEquals(externalData, e.Data))
                {
                    var files = e.Data.GetData(DataFormats.FileDrop, false) as string[];
                    externalPins = files == null ? null : ui.Platform.ResolveDrop(files);
                    externalData = e.Data;
                }
                if (externalPins == null || externalPins.Length == 0) return;
                items.Finish();
                e.Effects = effect;
                if (commit)
                {
                    bool after;
                    string anchor = DropAnchor(e.GetPosition(strip).X, null, out after);
                    ui.DropFiles(externalPins, anchor, after);
                }
            }
            catch (COMException) { e.Effects = DragDropEffects.None; }
            finally { if (commit) ClearExternalDrop(); }
        }

        private List<string> ProposedPins()
        {
            var order = ui.Settings.Pins.Where(id => !DesktopUi.IdEqual(id,drag.Id)).ToList();
            int anchor = order.FindIndex(id => DesktopUi.IdEqual(id, drag.Target));
            order.Insert(anchor < 0 ? order.Count : anchor + (drag.After ? 1 : 0), drag.Id);
            return order;
        }

        private void PreviewOrder(List<string> order)
        {
            var reordered = slots.OrderBy(slot => {
                int index = order.FindIndex(id => DesktopUi.IdEqual(id,slot.Id));
                return index < 0 ? Int32.MaxValue : index;
            }).ToArray();
            for (int i=0; i<reordered.Length; i++)
            {
                var slot = reordered[i];
                bool picked = drag.Surface == this && DesktopUi.IdEqual(slot.Id,drag.Id);
                if (!picked) Shift(slot, slots[i].Left-slot.Left, true);
                slot.Offset.Y = picked ? -3 : 0;
                Panel.SetZIndex(slot.Button, picked ? 1 : 0);
            }
        }

        private void FinishDrop(object sender, DragEventArgs e)
        {
            if (drag == null) { ExternalDrop(e, true); return; }
            if (!e.Data.GetDataPresent(PinData)) return;
            TrackDrop(sender,e);
            var drop = drag;
            bool moved = !ProposedPins().SequenceEqual(ui.Settings.Pins,StringComparer.OrdinalIgnoreCase);
            if (!moved) { RestoreOrder(); return; }
            drag = null;
            // Clear provisional transforms before the controller re-renders every affected
            // surface. Only this commit invokes persistence.
            foreach (var dock in drop.Docks)
                foreach (var slot in dock.slots) {
                    Shift(slot,0,false); slot.Offset.Y=0; Panel.SetZIndex(slot.Button,0);
                }
            ui.DropPin(drop.Id, drop.Target, drop.After);
        }
        private void AttachMenu(FrameworkElement element, Action<ContextMenu> populate)
        {
            var menu = Visuals.Menu();
            menu.Placement = PlacementMode.Top;
            menu.Opened += delegate {
                shownMenu = menu; menu.Items.Clear(); populate(menu);
            };
            menu.Closed += delegate { if (shownMenu == menu) shownMenu = null; };
            element.ContextMenu = menu;
        }

        private void Popup(FrameworkElement anchor, ContextMenu menu)
        {
            shownMenu = menu; menu.PlacementTarget = anchor; menu.Placement = PlacementMode.Top;
            menu.Closed += delegate { if (shownMenu == menu) shownMenu = null; };
            menu.IsOpen = true;
        }

        private MenuItem SettingsMenuItem()
        {
            string caption = Loc.Get("SettingsMenu");
            var item = Visuals.MenuItem(caption, delegate { ui.OpenPreferences(screen); });
            Visuals.Identify(item, "Dock.Settings", caption);
            return item;
        }

        private void FillMainMenu(ContextMenu menu)
        {
            menu.Items.Add(Visuals.MenuItem(Loc.Get("OpenStart"), delegate { ui.ShowLaunchpad(screen); }));
            menu.Items.Add(SettingsMenuItem());
            menu.Items.Add(new Separator());
            menu.Items.Add(Visuals.MenuItem(Loc.Get("Quit"), ui.Exit));
        }

        private void FillAppMenu(ItemsControl menu, AppEntry app)
        {
            var windows = ui.WindowsFor(app.Id);
            FillWindows(menu, windows);
            if (windows.Count > 0) menu.Items.Add(new Separator());
            menu.Items.Add(Visuals.MenuItem(Loc.Get("LaunchApp"), delegate { ui.Launch(app); }));
            menu.Items.Add(Visuals.MenuItem(ui.Pinned(app.Id) ? Loc.Get("Unpin") : Loc.Get("Pin"),
                delegate { ui.TogglePin(app); }));
            string pin = ui.PinKey(app.Id);
            if (pin != null && ui.Settings.Pins.Count > 1)
            {
                int i = ui.Settings.Pins.FindIndex(id => DesktopUi.IdEqual(id, pin));
                if (i > 0) menu.Items.Add(Visuals.MenuItem(Loc.Get("MoveLeft"),
                    delegate { ui.DropPin(pin, ui.Settings.Pins[i - 1], false); }));
                if (i < ui.Settings.Pins.Count - 1) menu.Items.Add(Visuals.MenuItem(Loc.Get("MoveRight"),
                    delegate { ui.DropPin(pin, ui.Settings.Pins[i + 1], true); }));
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(SettingsMenuItem());
            menu.Items.Add(Visuals.MenuItem(Loc.Get("Quit"), ui.Exit));
        }

        private void FillWindows(ItemsControl menu, IEnumerable<WindowEntry> windows)
        {
            foreach (var entry in windows)
            {
                var item = Visuals.MenuItem(entry.Title, delegate { ui.Activate(entry); });
                item.ToolTip = Visuals.Tip(entry.Title);
                item.Header = new TextBlock { Text = entry.Title, FontFamily = UiFonts.Text, MaxWidth = 420,
                    TextTrimming = TextTrimming.CharacterEllipsis };
                System.Windows.Automation.AutomationProperties.SetName(item, entry.Title);
                menu.Items.Add(item);
            }
        }
    }
}
