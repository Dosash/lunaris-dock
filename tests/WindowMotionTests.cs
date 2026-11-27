using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
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

    [STAThread]
    public static int Main()
    {
        try {
            if (SystemParameters.ClientAreaAnimation) { ThemeFade(); PreferencesFade(); }
            else Console.WriteLine("INFO Windows has disabled motion on this desktop.");
            return 0;
        } catch (Exception ex) { Console.WriteLine(ex); return 1; }
    }
}
