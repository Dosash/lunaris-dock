using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Navigation;
using Lunaris;

internal static class ReleaseUpdateTests
{
    const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;
    const BindingFlags Static = BindingFlags.NonPublic | BindingFlags.Static;
    static readonly Assembly Product = typeof(UserSettings).Assembly;
    static readonly Type Updates = Product.GetType("Lunaris.ReleaseUpdates", true);
    const string Project = "https://github.com/Dosash/lunaris-dock";

    static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }

    static string Body(string tag, bool draft, bool prerelease)
    {
        return "{\"tag_name\":\"" + tag + "\",\"draft\":" + (draft ? "true" : "false") +
            ",\"prerelease\":" + (prerelease ? "true" : "false") + ",\"html_url\":\"https://invalid.example\"}";
    }

    static object Read(string tag, bool draft = false, bool prerelease = false)
    {
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(Body(tag, draft, prerelease))))
            return Updates.GetMethod("ReadRelease", Static).Invoke(null, new object[] { stream, new Version(1,9,0) });
    }

    sealed class Reply : HttpMessageHandler
    {
        internal HttpStatusCode Status;
        internal string Content;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri.AbsoluteUri != "https://api.github.com/repos/Dosash/lunaris-dock/releases/latest" ||
                request.Headers.UserAgent.Count == 0 || request.Headers.Authorization != null)
                throw new Exception("Unexpected release request.");
            return Task.FromResult(new HttpResponseMessage(Status) {Content = new StringContent(Content)});
        }
    }

    static object Fetch(HttpStatusCode status, string json)
    {
        using (var service = (IDisposable)Activator.CreateInstance(Updates, Instance, null,
            new object[] {new Reply {Status = status, Content = json}}, null))
        {
            var task = (Task)Updates.GetMethod("LatestAsync", Instance).Invoke(service, null);
            task.GetAwaiter().GetResult();
            return task.GetType().GetProperty("Result").GetValue(task, null);
        }
    }

    static void Responses()
    {
        var version = Product.GetName().Version;
        string displayed = version.ToString(version.Revision > 0 ? 4 : 3);
        Require((string)Updates.GetField("CurrentVersion", Static).GetValue(null) == displayed, "current version comes from the executable");
        var result = Read("v1.10.0");
        Require((string)result.GetType().GetField("Version", Instance).GetValue(result) == "1.10.0" &&
            (string)result.GetType().GetField("Url", Instance).GetValue(result) == Project + "/releases/tag/v1.10.0",
            "numeric ordering and canonical project link ignore the API's external URL");
        var revision = Read("1.9.0.1");
        Require(revision != null && (string)revision.GetType().GetField("Version", Instance).GetValue(revision) == "1.9.0.1",
            "nonzero fourth version component is compared and displayed");
        Require(Read("v1.9.0") == null && Read("1.9.0.0") == null && Read("v1.8.9") == null &&
            Read("v2.0.0", true) == null && Read("v2.0.0", false, true) == null, "equal, older, draft and prerelease responses are skipped");
        Require(new[]{"", "v2", "v2.0.0-beta", "../v2.0.0", "v2.0.0/elsewhere", "v999999999999.0.0"}.All(tag => Read(tag) == null),
            "invalid release tags cannot become links");
        Require(Fetch(HttpStatusCode.OK, Body("v999.0.0", false, false)) != null &&
            Fetch(HttpStatusCode.NotFound, "{}") == null, "anonymous latest-release request and repository without published releases");
        try { Fetch(HttpStatusCode.Forbidden, "rate limited"); throw new Exception("Rate limit treated as a successful response."); }
        catch (HttpRequestException) { Console.WriteLine("PASS rate limit reported as an unsuccessful check"); }
        try { Fetch(HttpStatusCode.OK, "{}"); throw new Exception("Incomplete JSON accepted as a release."); }
        catch (SerializationException) { Console.WriteLine("PASS incomplete release metadata rejected"); }
    }

    static DependencyObject WithId(DependencyObject root, string id)
    {
        if (AutomationProperties.GetAutomationId(root) == id) return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            var found = WithId(child, id);
            if (found != null) return found;
        }
        return null;
    }

    static void Support(Window prefs, FrameworkElement view)
    {
        var title = (TextBlock)view.FindName("SupportTitle");
        var description = (TextBlock)view.FindName("SupportDescription");
        var link = (Hyperlink)view.FindName("SupportLink");
        var caption = link.Inlines.OfType<Run>().Single();
        Require(link.NavigateUri.AbsoluteUri == "https://pay.cloudtips.ru/p/894f4248" && link.Focusable && link.IsEnabled,
            "support link is keyboard accessible and targets the author's CloudTips page");

        var locales = new[] {
            new[]{"en","💖 Support","CloudTips (card)"}, new[]{"ru","💖 Поддержать","CloudTips (карта)"},
            new[]{"de","💖 Unterstützen","CloudTips (Kartenzahlung)"}, new[]{"es","💖 Apoyar el proyecto","CloudTips (tarjeta)"},
            new[]{"zh-CN","💖 支持开发","CloudTips（银行卡）"}
        };
        var loc = Product.GetType("Lunaris.Loc",true);
        string previous = (string)loc.GetProperty("Language",Static).GetValue(null,null);
        Action<string> switchLanguage = code => {
            loc.GetMethod("Use",Static).Invoke(null,new object[]{code});
            prefs.GetType().GetMethod("ApplyLanguage",Instance).Invoke(prefs,null);
        };
        var descriptions = new System.Collections.Generic.HashSet<string>();
        foreach (var locale in locales)
        {
            switchLanguage(locale[0]);
            Require(title.Text == locale[1] && caption.Text == locale[2] && AutomationProperties.GetName(link) == locale[2] &&
                description.Text.Contains("Lunaris Dock") && descriptions.Add(description.Text),
                "support heading, description and accessible link change in the same settings view: " + locale[0]);
            if (locale[0] == "ru")
                Require(description.Text == "Если вам нравится Lunaris Dock, вы можете поблагодарить автора и поддержать разработку.",
                    "Russian support copy matches the requested wording");
        }
        switchLanguage(previous);

        // Class handlers run before the view's instance handler. Exercise the actual
        // Hyperlink click and intercept its navigation inside this test process.
        int clicks = 0;
        var probe = new Uri("about:blank");
        link.NavigateUri = probe;
        EventManager.RegisterClassHandler(typeof(Hyperlink), Hyperlink.RequestNavigateEvent,
            new RequestNavigateEventHandler(delegate(object sender, RequestNavigateEventArgs e) {
                if (sender == link && e.Uri == probe) { e.Handled = true; clicks++; }
            }));
        link.DoClick();
        Require(clicks == 1,"support hyperlink activation requests browser navigation without visiting the payment page");
    }

    static void Surfaces()
    {
        var settings = new UserSettings();
        int writes = 0;
        using (var platform = new PlatformServices(true))
        using (var ui = new DesktopUi(platform,settings,delegate {writes++;},delegate {}))
        {
            var prefsType = Product.GetType("Lunaris.Preferences", true);
            var prefs = (Window)Activator.CreateInstance(prefsType, Instance, null, new object[]{ui}, null);
            typeof(DesktopUi).GetField("preferences", Instance).SetValue(ui,prefs);
            var view = (FrameworkElement)prefs.Content;
            Support(prefs,view);
            var link = (Button)view.FindName("ReleaseLink");
            string version = (string)Updates.GetField("CurrentVersion", Static).GetValue(null);
            Require(link.Visibility == Visibility.Collapsed && (string)((Button)view.FindName("ProjectLink")).ToolTip == Project &&
                ((TextBlock)view.FindName("CurrentVersion")).Text.Contains(version), "settings expose app version and project before a release is available");
            var publish = typeof(DesktopUi).GetMethod("SetAvailableUpdate", Instance);
            var newer = Read("v2.0.0");
            publish.Invoke(ui, new[]{newer});
            Require(link.Visibility == Visibility.Visible && link.Content.ToString().Contains("2.0.0") &&
                (string)link.ToolTip == Project + "/releases/tag/v2.0.0" && writes == 0,
                "release notification updates the existing settings without saving the profile");
            var screenType = Product.GetType("Lunaris.ScreenInfo", true);
            var bounds = new Rect(-32000,-32000,1000,600);
            var screen = Activator.CreateInstance(screenType, Instance, null, new object[]{"test","test",true,bounds,bounds,1.0}, null);
            var dockType = Product.GetType("Lunaris.DockSurface", true);
            var dock = (Window)Activator.CreateInstance(dockType, Instance, null, new[]{(object)ui,screen}, null);
            try
            {
                var render = dockType.GetMethod("Render", Instance | BindingFlags.DeclaredOnly);
                render.Invoke(dock, new[]{screen});
                var start = WithId((DependencyObject)dock.Content,"OpenLaunchpad");
                Require(start != null && WithId(start,"Dock.UpdateBadge") != null &&
                    AutomationProperties.GetItemStatus(start).Contains("2.0.0"), "Start button shows a visible release badge and accessible version");
                settings.ShowUpdateBadge = false;
                render.Invoke(dock, new[]{screen});
                Require(WithId((DependencyObject)dock.Content,"Dock.UpdateBadge") == null && link.Visibility == Visibility.Visible,
                    "disabling the badge keeps the release available in preferences");
                publish.Invoke(ui,new object[]{null});
                Require(link.Visibility == Visibility.Collapsed && writes == 0, "withdrawn release clears the notification without saving the profile");
            }
            finally { dock.Close(); }
        }
    }

    [STAThread]
    public static int Main()
    {
        try { Responses(); Surfaces(); return 0; }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
    }
}
