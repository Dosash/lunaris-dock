using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Lunaris
{
    internal sealed class DockItemMotion
    {
        internal sealed class Item
        {
            internal FrameworkElement Element;
            internal string Key;
            internal double Scale, Opacity, Center;
        }

        // A departing visual has no Button, mouse handlers, tooltip or UIA command.
        private sealed class Departure : Border { }
        private readonly StackPanel strip;
        private readonly List<Action> cleanup = new List<Action>();
        private readonly DispatcherTimer timer;
        private Action settled;
        private const int Duration = 230;

        internal DockItemMotion(StackPanel strip)
        {
            this.strip = strip;
            timer = new DispatcherTimer(DispatcherPriority.Render, strip.Dispatcher) { Interval = TimeSpan.FromMilliseconds(Duration + 25) };
            timer.Tick += delegate { Finish(); };
        }

        internal List<Item> Capture(bool animate)
        {
            List<Item> items = null;
            if (animate)
            {
                strip.UpdateLayout();
                items = new List<Item>();
                foreach (FrameworkElement child in strip.Children)
                {
                    var scale = child.LayoutTransform as ScaleTransform;
                    items.Add(new Item { Element = child, Key = (string)child.Tag, Opacity = child.Opacity,
                        Scale = scale == null ? 1 : scale.ScaleX, Center = Center(child) });
                }
            }
            Cancel();
            return items;
        }

        private double Center(FrameworkElement child)
        {
            return child.TranslatePoint(new Point(child.ActualWidth / 2, 0), strip).X - strip.ActualWidth / 2;
        }

        internal bool Play(List<Item> before, bool pinCountChanged, Action complete)
        {
            if (before == null || before.Count == 0) return false;
            var old = before.ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);
            var live = strip.Children.Cast<FrameworkElement>().ToDictionary(e => (string)e.Tag, StringComparer.OrdinalIgnoreCase);
            if (!pinCountChanged && live.Count == old.Count && live.Keys.All(old.ContainsKey) &&
                before.All(item => item.Scale == 1 && !(item.Element is Departure))) return false;

            // Insert shrinking placeholders at their former neighbours. Animating
            // layout scale reserves just the visible width and moves each neighbour
            // and the centred plate together, including across the overflow boundary.
            var starts = new List<Action>();
            int insertAt = strip.Children.Count;
            for (int i = before.Count - 1; i >= 0; i--)
            {
                Item item = before[i];
                FrameworkElement next;
                if (live.TryGetValue(item.Key, out next)) insertAt = strip.Children.IndexOf(next);
                else
                {
                    var ghost = Ghost(item);
                    strip.Children.Insert(insertAt, ghost);
                    SizeAndFade(ghost, item.Scale, item.Opacity, true, starts);
                    cleanup.Add(delegate { strip.Children.Remove(ghost); });
                }
            }
            foreach (var pair in live)
            {
                Item item;
                if (!old.TryGetValue(pair.Key, out item)) SizeAndFade(pair.Value, .001, 0, false, starts);
                else if (item.Scale < .999 || item.Element is Departure)
                    SizeAndFade(pair.Value, item.Scale, item.Opacity, false, starts);
            }

            // Pinning a running app can move a surviving item across the divider.
            // Match its displayed position before animating into its new slot.
            strip.Measure(new Size(Double.PositiveInfinity, Double.PositiveInfinity));
            strip.Arrange(new Rect(strip.DesiredSize));
            foreach (var pair in live)
            {
                Item item;
                if (!old.TryGetValue(pair.Key, out item) || item.Scale < .999) continue;
                var child = pair.Value;
                double offset = item.Center - Center(child);
                if (Math.Abs(offset) < 1) continue;
                var original = child.RenderTransform;
                var shift = new TranslateTransform(offset, 0);
                var group = new TransformGroup();
                group.Children.Add(original);
                group.Children.Add(shift);
                child.RenderTransform = group;
                starts.Add(delegate { Animate(shift, TranslateTransform.XProperty, offset, 0); });
                cleanup.Add(delegate { child.RenderTransform = original; });
            }
            if (starts.Count == 0) return false;
            foreach (var start in starts) start();
            settled = complete;
            timer.Start();
            return true;
        }

        private static Departure Ghost(Item item)
        {
            var previous = item.Element;
            var button = previous as Button;
            var border = previous as Border;
            Brush fill = button == null ? border.Background : new VisualBrush(button.Content as Visual) { Stretch = Stretch.Fill };
            return new Departure { Tag = item.Key, Background = fill, Width = previous.ActualWidth, Height = previous.ActualHeight,
                Margin = previous.Margin, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Focusable = false };
        }

        private static void Animate(IAnimatable target, DependencyProperty property, double from, double to)
        {
            var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(Duration)) {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.HoldEnd
            };
            target.BeginAnimation(property, animation);
        }

        private void SizeAndFade(FrameworkElement child, double from, double opacity, bool leaving, List<Action> starts)
        {
            double endOpacity = leaving ? 0 : child.Opacity;
            double end = leaving ? .001 : 1;
            var scale = new ScaleTransform(from, from);
            child.LayoutTransform = scale;
            child.Opacity = opacity;
            starts.Add(delegate {
                Animate(scale, ScaleTransform.ScaleXProperty, from, end);
                Animate(scale, ScaleTransform.ScaleYProperty, from, end);
                Animate(child, UIElement.OpacityProperty, opacity, endOpacity);
            });
            cleanup.Add(delegate {
                child.ClearValue(FrameworkElement.LayoutTransformProperty);
                child.BeginAnimation(UIElement.OpacityProperty, null);
                child.Opacity = endOpacity;
            });
        }

        internal void Cancel()
        {
            timer.Stop();
            foreach (var reset in cleanup) reset();
            cleanup.Clear();
            settled = null;
        }

        internal void Finish()
        {
            if (settled == null) return;
            var complete = settled;
            Cancel();
            complete();
        }
    }
}
