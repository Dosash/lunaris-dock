using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Lunaris
{
    internal sealed class QuickRadioPanel : StackPanel
    {
        private readonly StackPanel switches = new StackPanel();
        private readonly TextBlock message = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        private readonly Action resize, networkChanged;
        private string[] networkStates;
        private bool reading, changing, updatingChecks;
        private int revision;

        internal QuickRadioPanel(Action resize, Action networkChanged)
        {
            this.resize = resize;
            this.networkChanged = networkChanged;
            Children.Add(switches);
            Children.Add(message);
            message.SetResourceReference(TextBlock.ForegroundProperty, "MenuText");
            message.Text = Loc.Get("StatusLoading");
            timer.Tick += delegate { Refresh(); };
            Loaded += delegate { timer.Start(); Refresh(); };
            Unloaded += delegate { timer.Stop(); revision++; };
        }

        private async void Refresh()
        {
            if (reading || changing || !IsLoaded) return;
            reading = true;
            int request = revision;
            try {
                var airplane = Task.Run(() => {
                    try { return SystemRadio.Airplane(); }
                    catch (Exception) { return new SystemRadio { Kind = "Airplane" }; }
                });
                List<SystemRadio> rows;
                try { rows = await SystemRadio.Wireless(); }
                catch (Exception) { rows = new List<SystemRadio>(); }
                if (!rows.Any(r => r.Kind == "Bluetooth")) rows.Add(new SystemRadio { Kind = "Bluetooth" });
                rows = rows.OrderBy(r => r.Kind).ThenBy(r => r.Name).ToList();
                rows.Add(await airplane);
                if (!IsLoaded || revision != request) return;
                while (switches.Children.Count > rows.Count) switches.Children.RemoveAt(switches.Children.Count - 1);
                for (int i = 0; i < rows.Count; i++) {
                    var radio = rows[i];
                    CheckBox toggle;
                    if (i < switches.Children.Count) toggle = (CheckBox)switches.Children[i];
                    else {
                        toggle = new CheckBox();
                        StatusMenuChrome.StyleSwitch(toggle);
                        toggle.Checked += Change;
                        toggle.Unchecked += Change;
                        switches.Children.Add(toggle);
                    }
                    string name = radio.Kind == "Bluetooth" ? "Bluetooth" : radio.Kind == "WifiRadio" ? "Wi-Fi" : Loc.Get("QuickAirplane");
                    if (rows.Count(r => r.Kind == radio.Kind) > 1) name += " · " + radio.Name;
                    toggle.Tag = radio;
                    string label = radio.Enabled.HasValue ? name : name + " · " + Loc.Get("QuickUnavailable");
                    if (AutomationProperties.GetName(toggle) != label || AutomationProperties.GetAutomationId(toggle) != "StatusMenu." + radio.Kind) {
                        var line = new DockPanel();
                        var icon = StatusMenuChrome.Glyph(radio.Kind == "Bluetooth" ? "\uE702" :
                            radio.Kind == "WifiRadio" ? "\uE701" : "\uE709", 16, "MenuText");
                        icon.Width = 22;
                        icon.Margin = new Thickness(0,0,7,0);
                        line.Children.Add(icon);
                        line.Children.Add(StatusMenuChrome.Label(label,14,"MenuText"));
                        toggle.Content = line;
                        Visuals.Identify(toggle, "StatusMenu." + radio.Kind, label);
                    }
                    toggle.IsEnabled = radio.Enabled.HasValue;
                    ShowChecked(toggle, radio.Enabled);
                    AutomationProperties.SetItemStatus(toggle, radio.Enabled.HasValue ? (radio.Enabled.Value ? "on" : "off") : "unavailable");
                }
                var nextStates = rows.Where(r => r.Kind != "Bluetooth")
                    .Select(r => r.Kind + "/" + r.Name + "/" + r.Enabled).ToArray();
                bool changed = networkStates != null && !networkStates.SequenceEqual(nextStates);
                networkStates = nextStates;
                if (message.Text == Loc.Get("StatusLoading")) message.Text = "";
                message.Visibility = message.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                if (changed) networkChanged();
                resize();
            } finally { reading = false; }
        }

        private void ShowChecked(CheckBox toggle, bool? state)
        {
            updatingChecks = true;
            try { toggle.IsChecked = state; }
            finally { updatingChecks = false; }
        }

        private async void Change(object sender, RoutedEventArgs e)
        {
            if (updatingChecks) return;
            var toggle = (CheckBox)sender;
            var radio = (SystemRadio)toggle.Tag;
            bool requested = toggle.IsChecked == true;
            ShowChecked(toggle, radio.Enabled);
            if (changing || !radio.Enabled.HasValue) return;
            revision++;
            changing = true;
            switches.IsEnabled = false;
            bool accepted = false;
            try { accepted = await radio.Set(requested); }
            catch (Exception) { /* Windows can deny access or remove the adapter mid-request. */ }
            finally { changing = false; }
            if (!IsLoaded) return;
            switches.IsEnabled = true;
            message.Text = accepted ? "" : Loc.Get("QuickDenied");
            message.Visibility = accepted ? Visibility.Collapsed : Visibility.Visible;
            Refresh();
            resize();
        }
    }
}
