using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lunaris;

internal static class WindowMotionTests
{
    const BindingFlags Internal = BindingFlags.NonPublic | BindingFlags.Instance;
    static readonly Assembly Product = typeof(UserSettings).Assembly;

    static void Observe(int duration, Action sample)
    {
        var frame = new DispatcherFrame();
        var elapsed = Stopwatch.StartNew();
        var tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        tick.Tick += delegate {
            sample();
            if (elapsed.ElapsedMilliseconds >= duration) { tick.Stop(); frame.Continue = false; }
        };
        tick.Start();
        Dispatcher.PushFrame(frame);
    }

    static byte RedAt(BitmapSource bitmap)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(bitmap.PixelWidth/2,bitmap.PixelHeight/2,1,1),pixel,4,0);
        return pixel[2];
    }

    static void ThemeFade()
    {
        var root = new Grid { Background = Brushes.Red };
        var window = new Window { Content = root, Width = 180, Height = 120, Left = -31000, Top = -31000, ShowActivated = false,
            AllowsTransparency = true, WindowStyle = WindowStyle.None, ShowInTaskbar = false };
        try {
            window.Show(); window.UpdateLayout(); Observe(60,delegate {});
            var type = Product.GetType("Lunaris.ThemeTransition",true);
            var begin = type.GetMethod("Begin",BindingFlags.NonPublic | BindingFlags.Static);
            var image = type.GetField("image",Internal);
            var layer = AdornerLayer.GetAdornerLayer(root);
            Func<Adorner> overlay = () => (layer.GetAdorners(root) ?? new Adorner[0]).SingleOrDefault(x => x.GetType() == type);
            begin.Invoke(null,new object[]{window});
            var first = overlay();
            if (first == null || first.IsHitTestVisible || RedAt((BitmapSource)image.GetValue(first)) < 250)
                throw new Exception("The displayed old palette was not captured on a click-through overlay.");
            root.Background = Brushes.Blue;
            bool intermediate = false;
            Observe(115, delegate { intermediate |= first.Opacity > .05 && first.Opacity < .96; });
            if (!intermediate) throw new Exception("No intermediate theme colour blend.");
            double oldWeight = first.Opacity;
            begin.Invoke(null,new object[]{window});
            var next = overlay();
            if (next == first || next == null || Math.Abs(RedAt((BitmapSource)image.GetValue(next)) - 255*oldWeight) > 20)
                throw new Exception("Rapid theme reversal did not begin with the displayed blend.");
            root.Background = Brushes.Lime;
            Observe(390, delegate {});
            if (overlay() != null) throw new Exception("The old palette still covers the final controls.");
            Console.WriteLine("PASS theme change blends captured colours, retains the current frame on reversal and releases the overlay");
        } finally { window.Close(); }
    }

    static void PreferencesFade()
    {
        using (var platform = new PlatformServices(true))
        using (var ui = new DesktopUi(platform,new UserSettings(),delegate{},delegate{}))
        {
            var type = Product.GetType("Lunaris.Preferences",true);
            var window = (Window)Activator.CreateInstance(type,Internal,null,new object[]{ui},null);
            window.Left = -31000; window.Top = -31000; window.ShowActivated = false; window.Topmost = false;
            var open = type.GetMethod("Present",Internal);
            var root = (FrameworkElement)window.Content;
            bool opening = false, closing = false, closed = false;
            window.Closed += delegate { closed = true; };
            try {
                open.Invoke(window,null);
                Observe(290,delegate { opening |= root.Opacity > .01 && root.Opacity < .99 && ((TranslateTransform)root.RenderTransform).Y > .01; });
                if (!opening || root.Opacity != 1) throw new Exception("Settings did not finish its entrance.");
                window.Close();
                Observe(70,delegate { closing |= root.Opacity > .01 && root.Opacity < .99; });
                if (closed || !closing) throw new Exception("Settings disappeared without fading.");
                open.Invoke(window,null);
                Observe(300,delegate {});
                if (closed || root.Opacity != 1 || !root.IsHitTestVisible) throw new Exception("Reopening settings during closing lost the window.");
                window.Close(); Observe(320,delegate {});
                if (!closed) throw new Exception("Settings did not close.");
                Console.WriteLine("PASS settings fade and translate, reverse an interrupted close and release the window on exit");
            } finally { if (!closed) type.GetMethod("CloseImmediately",Internal).Invoke(window,null); }
        }
    }

    static void LaunchFromButton()
    {
        using (var platform = new PlatformServices(true))
        using (var ui = new DesktopUi(platform, new UserSettings(), delegate {}, delegate {}))
        {
            var screenType = Product.GetType("Lunaris.ScreenInfo", true);
            var display = new Rect(-31000, -31000, 1800, 1000);
            var screen = Activator.CreateInstance(screenType, Internal, null,
                new object[] { "test", "test", true, display, display, 1.25 }, null);
            var panel = new Rect(-30600, -30800, 845, 660);
            var button = new Rect(-30685, -30114, 50, 50);
            var type = Product.GetType("Lunaris.LaunchSurface", true);
            var window = (Window)Activator.CreateInstance(type, Internal, null, new object[] { ui }, null);
            try {
                window.Left = display.Left; window.Top = display.Top; window.ShowActivated = false;
                window.Topmost = false; window.Show();
                type.GetField("panelScreen", Internal).SetValue(window, screen);
                type.GetField("lastBounds", Internal).SetValue(window, panel);
                type.GetField("launchButton", Internal).SetValue(window, button);
                var surface = (FrameworkElement)window.Content;
                var scale = (ScaleTransform)surface.RenderTransform;
                var current = type.GetProperty("CurrentScale", Internal);
                Func<double> visibleScale = () => (double)current.GetValue(window, null);
                var animate = type.GetMethod("Animate", Internal);
                animate.Invoke(window, new object[] { true, false });
                var first = visibleScale();
                var centre = new ScaleTransform(first,first,scale.CenterX,scale.CenterY).Transform(
                    new Point(panel.Width / 2.5, panel.Height / 2.5));
                if (Math.Abs(panel.X + centre.X * 1.25 - button.X - button.Width/2) > 1 ||
                    Math.Abs(panel.Y + centre.Y * 1.25 - button.Y - button.Height/2) > 1)
                    throw new Exception("First frame misses the button outside the final panel.");
                bool moving = false;
                Observe(120, delegate { double sample = visibleScale(); moving |= sample > .1 && sample < 1; });
                if (!moving) throw new Exception("Missing intermediate launcher scale.");
                animate.Invoke(window, new object[] { false, true });
                Observe(55, delegate {});
                double before = visibleScale();
                animate.Invoke(window, new object[] { true, true });
                if (Math.Abs(visibleScale() - before) > .03) throw new Exception("Interrupted launcher jumps from " + before + " to " + visibleScale());
                Observe(400, delegate {});
                if (!window.IsVisible || scale.ScaleX != 1 || surface.Opacity != 1 ||
                    surface.CacheMode != null || surface.Margin != new Thickness())
                    throw new Exception("A stale launcher animation hid or resized the reopened panel.");
                Console.WriteLine("PASS Start grows from the real button at 125% DPI, reverses and settles at full resolution");
            } finally { window.Close(); }
        }
    }

    static void FramePacing()
    {
        var times = new List<double>();
        var type = Product.GetType("Lunaris.AnimationFrameClock", true);
        object clock = Activator.CreateInstance(type, Internal, null,
            new object[]{Dispatcher.CurrentDispatcher, new Action<double>(times.Add)}, null);
        try { Observe(300, delegate {}); }
        finally { type.GetMethod("Stop", Internal).Invoke(clock, null); }
        int count = times.Count;
        Observe(60, delegate {});
        if (count < 2 || times.Count != count) throw new Exception("Frame clock failed to run or stop.");
        var intervals = times.Skip(1).Select((value,i) => value-times[i]).OrderBy(value=>value).ToArray();
        if (intervals[0] < 1.0/60 - 1e-7) throw new Exception("Frame clock exceeded 60 FPS.");
        Console.WriteLine("PASS WPF fallback frame clock, minimum {0:F2} ms, median {1:F2} ms, no callbacks after cancellation",
            intervals[0]*1000, intervals[intervals.Length/2]*1000);
    }

    [STAThread]
    public static int Main()
    {
        try {
            var label = Product.GetType("Lunaris.KeyboardTarget").GetMethod("Label", BindingFlags.NonPublic | BindingFlags.Static);
            if ((string)label.Invoke(null, new object[]{IntPtr.Zero}) != "⌨") throw new Exception("Missing foreground keyboard layout.");
            Product.GetType("Lunaris.Program").GetMethod("ConfigureAnimationTiming", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null,null);
            if (Timeline.GetDesiredFrameRate(new DoubleAnimation()) != 60 || Timeline.GetDesiredFrameRate(new Storyboard()) != 60)
                throw new Exception("WPF animation clocks lack the 60 FPS limit.");
            FramePacing();
            if (SystemParameters.ClientAreaAnimation) { ThemeFade(); PreferencesFade(); LaunchFromButton(); }
            else Console.WriteLine("INFO Windows has disabled motion on this desktop.");
            return 0;
        } catch (Exception ex) { Console.WriteLine(ex); return 1; }
    }
}
