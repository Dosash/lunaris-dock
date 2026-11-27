using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Lunaris;

internal static class PreferencesMotionTests
{
    private delegate bool EnumWindow(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, EnumWindow visitor, IntPtr data);
    static readonly Assembly Product = typeof(UserSettings).Assembly;
    static readonly MethodInfo Paint = Product.GetType("Lunaris.Preferences").GetMethod("PaintSwitch", BindingFlags.NonPublic | BindingFlags.Static);

    static void CheckMotion(CheckBox box, bool on)
    {
        var knob = (Border)box.Template.FindName("thumb", box);
        var tint = (Border)box.Template.FindName("onTrack", box);
        var offset = (TranslateTransform)knob.RenderTransform;
        box.IsChecked = on;
        Paint.Invoke(null, new object[] {box, true});
        var samples = new List<double>();
        bool tintAdvanced = false;
        var frame = new DispatcherFrame();
        var clock = Stopwatch.StartNew();
        var sampler = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(12) };
        sampler.Tick += delegate {
            samples.Add(offset.X);
            tintAdvanced |= tint.Opacity > 0.01 && tint.Opacity < 0.99;
            if (clock.ElapsedMilliseconds >= 270) { sampler.Stop(); frame.Continue = false; }
        };
        sampler.Start();
        Dispatcher.PushFrame(frame);
        double end = on ? 14 : 0;
        if (!tintAdvanced || !samples.Any(x => x > .1 && x < 13.9) || Math.Abs(offset.X-end) > .01 ||
            Math.Abs(tint.Opacity-(on ? 1 : 0)) > .01)
            throw new Exception("Switch did not traverse the track with a colour transition: " + String.Join(", ", samples));
        Console.WriteLine("PASS switch thumb and track animate through intermediate values " + (on ? "on" : "off"));
    }

    [STAThread]
    public static int Main()
    {
        try {
            FrameworkElement view;
            using (var stream = Product.GetManifestResourceStream("Lunaris.Preferences.xaml"))
                view = (FrameworkElement)XamlReader.Load(stream);
            using (var source = new HwndSource(new HwndSourceParameters("Lunaris switch test") {
                WindowStyle = unchecked((int)0x80000000), PositionX = -31000, PositionY = -31000, Width = 500, Height = 680
            })) {
                source.RootVisual = view;
                view.Measure(new Size(500,680)); view.Arrange(new Rect(0,0,500,680)); view.UpdateLayout();
                var box = (CheckBox)view.FindName("HideDockWhenCovered");
                box.ApplyTemplate();
                Paint.Invoke(null, new object[]{box, false});
                CheckMotion(box, true);
                CheckMotion(box, false);
                box.IsChecked = true;
                Paint.Invoke(null, new object[]{box, false});
                var knob = (Border)box.Template.FindName("thumb",box);
                if (((TranslateTransform)knob.RenderTransform).X != 14) throw new Exception("Motion-disabled switch has wrong position");
                Console.WriteLine("PASS motion-disabled switch moves directly to its final position");
            }
            var redirect = Product.GetType("Lunaris.WindowsStartRedirect");
            var identify = redirect.GetMethod("IsWindowsStart",BindingFlags.NonPublic | BindingFlags.Static);
            if ((bool)identify.Invoke(null,new object[]{IntPtr.Zero})) throw new Exception("Missing window accepted as native Start");
            using (var source = new HwndSource(new HwndSourceParameters("Lunaris foreground test") { WindowStyle=0, Width=1, Height=1 })) {
                if ((bool)identify.Invoke(null,new object[]{source.Handle})) throw new Exception("Unrelated window accepted as Start");
            }
            Console.WriteLine("PASS native Start routing ignores missing and unrelated windows");
            int inspected = 0;
            var identifyProcess = redirect.GetMethod("IsWindowsStartProcess",BindingFlags.NonPublic | BindingFlags.Static);
            foreach (var host in Process.GetProcessesByName("StartMenuExperienceHost"))
            using (host) {
                if (!(bool)identifyProcess.Invoke(null, new object[]{(uint)host.Id})) throw new Exception("Genuine Start host was not recognized");
                Console.WriteLine("PASS real Windows StartMenuExperienceHost recognized by its system image path");
                foreach (ProcessThread thread in host.Threads)
                using (thread) {
                    bool recognized = true;
                    EnumThreadWindows((uint)thread.Id, delegate(IntPtr hwnd, IntPtr data) {
                        inspected++;
                        recognized &= (bool)identify.Invoke(null,new object[]{hwnd});
                        return true;
                    }, IntPtr.Zero);
                    if (!recognized) throw new Exception("Native Windows Start host was not recognized");
                }
            }
            if (inspected != 0) Console.WriteLine("PASS native Start identity recognized on " + inspected + " genuine shell window handles");
            var methodFlags = BindingFlags.Instance | BindingFlags.NonPublic;
            object watch = Activator.CreateInstance(redirect, methodFlags, null,
                new object[]{Dispatcher.CurrentDispatcher, new Action(delegate {})}, null);
            using ((IDisposable)watch) {
                redirect.GetMethod("Enable",methodFlags).Invoke(watch,null);
                if ((IntPtr)redirect.GetField("hook",methodFlags).GetValue(watch) == IntPtr.Zero) throw new Exception("Foreground event subscription failed");
            }
            if ((IntPtr)redirect.GetField("hook",methodFlags).GetValue(watch) != IntPtr.Zero) throw new Exception("Foreground event subscription still active");
            Console.WriteLine("PASS native Start observer registers and releases its foreground notification");
            return 0;
        } catch (Exception ex) { Console.WriteLine(ex); return 1; }
    }
}
