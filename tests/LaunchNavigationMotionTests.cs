using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lunaris;

// Run through scripts/Invoke-TestProgram.ps1. All visuals remain off screen.
internal static class LaunchNavigationMotionTests
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object motion;
    private static Type motionType, directionType;
    private static Grid viewport, live;
    private static Border previous;
    private static bool enabled = true;
    private static int state, updates;

    [STAThread]
    private static void Main()
    {
        var assembly = typeof(UserSettings).Assembly;
        motionType = assembly.GetType("Lunaris.LaunchNavigationMotion", true);
        directionType = assembly.GetType("Lunaris.LaunchMove", true);
        motion = Activator.CreateInstance(motionType, Members, null, new object[] { new Func<bool>(() => enabled) }, null);
        viewport = (Grid)motionType.GetField("Viewport", Members).GetValue(motion);
        live = (Grid)motionType.GetField("Content", Members).GetValue(motion);
        previous = (Border)viewport.Children[0];
        SetState(1);
        viewport.Measure(new Size(300, 180));
        viewport.Arrange(new Rect(0, 0, 300, 180));
        var origin = new Point(.18, .74);

        Change("NextPage", origin, 2);
        Check(state == 2 && previous.Visibility == Visibility.Visible, "immediate navigation");
        Check(((TranslateTransform)live.RenderTransform).X > 0, "next page enters from right");
        Check(IsRed(OldPixel()), "outgoing snapshot belongs to the previous page");
        Check(!previous.IsHitTestVisible && !previous.Focusable &&
              UIElementAutomationPeer.CreatePeerForElement(previous) == null &&
              viewport.Children.Count == 2 && live.Children.Count == 1, "no outgoing hit target or UI Automation peer");

        Change("PreviousPage", origin, 1);
        Check(((TranslateTransform)live.RenderTransform).X < 0, "reverse input enters from left");
        Check(IsRed(OldPixel()), "reversal preserves the still-visible outgoing composition");
        Change("NextPage", origin, 2);
        Change("EnterFolder", origin, 3);
        Check(((ScaleTransform)live.RenderTransform).ScaleX < 1 && live.RenderTransformOrigin == origin, "folder opens from its tile");
        Change("LeaveFolder", origin, 1);
        Check(((ScaleTransform)live.RenderTransform).ScaleX > 1 && live.RenderTransformOrigin == origin, "folder returns toward its tile");
        Wait(360);
        CheckSettled("only final destination survives rapid navigation");
        Check(state == 1 && updates == 6, "logical changes run exactly once");

        Change("EnterFolder", origin, 3);
        motionType.GetMethod("Cancel", Members).Invoke(motion, null);
        viewport.Arrange(new Rect(0, 0, 220, 120));
        SetState(4);
        Wait(300);
        CheckSettled("old animation cannot overwrite a replacement layout or fresh search");
        Check(state == 4, "new results retained");
        Change("NextPage", origin, 2);
        enabled = false;
        Change("PreviousPage", origin, 1);
        CheckSettled("reduced motion interrupts previous transition immediately");
        Check(state == 1, "reduced motion still navigates");
        CheckLauncherInputs(assembly);
        Console.WriteLine("Launchpad navigation motion passed (off-screen WPF).");
    }

    private sealed class DetachedSource : PresentationSource
    {
        public override Visual RootVisual { get; set; }
        public override bool IsDisposed { get { return false; } }
        protected override CompositionTarget GetCompositionTargetCore() { return null; }
    }

    private static void CheckLauncherInputs(Assembly assembly)
    {
        var settings = new UserSettings();
        var desktop = new DesktopUi(null, settings, delegate { }, delegate { });
        var apps = (List<AppEntry>)typeof(DesktopUi).GetField("Catalog", Members).GetValue(desktop);
        for (int i = 0; i < 48; i++)
            apps.Add(new AppEntry { Name = "Sample " + i.ToString("D2"), Id = "mock-" + i, StartFolder = i == 0 ? "Office" : null });
        var type = assembly.GetType("Lunaris.LaunchSurface", true);
        var window = (Window)Activator.CreateInstance(type, Members, null, new object[] { desktop }, null);
        try {
            type.GetMethod("RefreshCatalog", Members, null, Type.EmptyTypes, null).Invoke(window, null);
            var search = (TextBox)type.GetField("search", Members).GetValue(window);
            var tiles = (Grid)type.GetField("grid", Members).GetValue(window);
            var pager = (Panel)type.GetField("pager", Members).GetValue(window);
            Func<int> page = () => (int)type.GetProperty("Page", Members).GetValue(window, null);
            Action<string> clickPage = id => pager.Children.OfType<Button>()
                .Single(b => AutomationProperties.GetAutomationId(b) == id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var source = new DetachedSource();
            Action<Key> press = key => window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) {
                RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Action<int,int> wheel = (delta,time) => window.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, time, delta) {
                RoutedEvent = Mouse.PreviewMouseWheelEvent });
            FocusManager.SetFocusedElement(window, search);
            clickPage("Launchpad.Next"); Check(page() == 1, "next button");
            clickPage("Launchpad.Page.3"); Check(page() == 2, "page dot");
            press(Key.Left); Check(page() == 1, "keyboard crosses page boundary");
            press(Key.Right); Check(page() == 2, "reverse keyboard crossing");
            press(Key.PageUp); press(Key.PageUp); Check(page() == 0, "repeated Page Up");
            wheel(-60, 100); Check(page() == 0, "high-resolution wheel retains fraction");
            wheel(-60, 110); wheel(-120, 111); Check(page() == 2, "wheel accepts consecutive detents");
            wheel(120, 112); Check(page() == 1, "wheel reverses immediately");
            clickPage("Launchpad.Previous"); Check(page() == 0, "previous button");
            Check(FocusManager.GetFocusedElement(window) == search, "paging preserves search focus");
            var folder = tiles.Children.OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "Launchpad.Folder");
            folder.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(tiles.Children.Count == 1 && AutomationProperties.GetName(tiles.Children[0]) == "Sample 00", "folder opens");
            press(Key.Back); Check(tiles.Children.Count == 20 && page() == 0, "keyboard returns to folder origin");
            clickPage("Launchpad.Page.3");
            search.Text = "Sample 00";
            Check(tiles.Children.Count == 1 && page() == 0 && AutomationProperties.GetName(tiles.Children[0]) == "Sample 00",
                "fresh search replaces last page immediately");
        }
        finally { window.Close(); }
    }

    private static void SetState(int value)
    {
        state = value;
        ++updates;
        live.Background = value == 1 ? Brushes.Red : value == 2 ? Brushes.Blue : Brushes.Green;
        live.Children.Clear();
        live.Children.Add(new Button { Content = value.ToString(), Width = 36, Height = 20,
            VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left });
    }

    private static void Change(string direction, Point origin, int target)
    {
        motionType.GetMethod("Change", Members).Invoke(motion, new object[] {
            Enum.Parse(directionType, direction), origin, new Action(() => SetState(target)) });
    }

    private static byte[] OldPixel()
    {
        var bitmap = (BitmapSource)((ImageBrush)previous.Background).ImageSource;
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(bitmap.PixelWidth / 2, bitmap.PixelHeight / 2, 1, 1), pixel, 4, 0);
        return pixel;
    }

    private static bool IsRed(byte[] pixel) { return pixel[2] > 210 && pixel[0] < 20; }

    private static void CheckSettled(string detail)
    {
        Check(previous.Background == null && previous.Visibility == Visibility.Collapsed &&
              live.RenderTransform.Value.IsIdentity && live.Opacity == 1 && live.Children.Count == 1, detail);
    }

    private static void Wait(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += delegate { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Check(bool ok, string detail)
    {
        if (!ok) throw new Exception(detail);
    }
}
