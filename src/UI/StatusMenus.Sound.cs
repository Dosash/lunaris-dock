using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Lunaris
{
    internal sealed partial class StatusMenus
    {
        internal void OpenSound(Button button)
        {
            if (!Begin(button, "Sound", "AudioTitle")) return;
            var window = popup;
            // Keep the controls alive while the default endpoint, external
            // volume, or attached hardware changes with the menu open.
            int revision = 0;
            bool reading = false;
            Action changed = delegate { revision++; };
            var output = new SoundSection(AudioEndpoints.Flow.Output, changed, Place);
            var input = new SoundSection(AudioEndpoints.Flow.Input, changed, Place);
            body.Children.Add(output.Section);
            body.Children.Add(StatusMenuChrome.Rule());
            body.Children.Add(input.Section);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            EventHandler refresh = async delegate {
                if (reading || popup != window) return;
                int before = revision;
                reading = true;
                try {
                    var devices = await Task.Run(new Func<AudioEndpoints.Snapshot>(AudioEndpoints.Read));
                    if (popup != window || before != revision) return;
                    output.Update(devices.Output);
                    input.Update(devices.Input);
                    Place();
                } finally { reading = false; }
            };
            timer.Tick += refresh;
            window.Closed += delegate { timer.Stop(); timer.Tick -= refresh; };
            Show();
            if (popup == window) { timer.Start(); refresh(null, EventArgs.Empty); }
        }

        private sealed class SoundSection
        {
            internal readonly GroupBox Section;
            private readonly AudioEndpoints.Flow flow;
            private readonly Action changed, place;
            private readonly Button chooser;
            private readonly StackPanel options = new StackPanel { Visibility = Visibility.Collapsed };
            private readonly StackPanel levels = new StackPanel { Visibility = Visibility.Collapsed };
            private readonly TextBlock amount, state, deviceName, disclosure;
            private readonly Slider slider;
            private readonly CheckBox mute;
            private readonly string prefix;
            private AudioEndpoint[] endpoints = new AudioEndpoint[0];
            private AudioEndpoint selected;
            private bool applying, selectionFailed;

            internal SoundSection(AudioEndpoints.Flow flow, Action changed, Action place)
            {
                this.flow = flow; this.changed = changed; this.place = place;
                bool input = flow == AudioEndpoints.Flow.Input;
                prefix = input ? "Input" : "Output";
                string heading = Loc.Get("Audio" + prefix);
                var panel = new StackPanel();
                amount = StatusMenuChrome.Label("", 13, "MenuMuted");
                var header = new DockPanel();
                var symbol = StatusMenuChrome.Glyph(input ? "\uE720" : "\uE767", 16, "MenuText");
                symbol.Width = 20;
                symbol.Margin = new Thickness(0,0,6,0);
                header.Children.Add(symbol);
                DockPanel.SetDock(amount, Dock.Right);
                header.Children.Add(amount);
                var headingLabel = StatusMenuChrome.Label(heading, 14, "MenuText");
                headingLabel.FontWeight = FontWeights.SemiBold;
                header.Children.Add(headingLabel);
                Section = new GroupBox { Header = header, Content = panel, MinHeight = 115 };
                chooser = Choice(prefix + "Device", heading, ToggleOptions);
                var deviceRow = new DockPanel();
                disclosure = StatusMenuChrome.Glyph("\uE70D", 11, "MenuMuted");
                DockPanel.SetDock(disclosure, Dock.Right);
                disclosure.Margin = new Thickness(8,0,0,0);
                deviceRow.Children.Add(disclosure);
                deviceName = StatusMenuChrome.Label("", 13, "MenuText");
                deviceRow.Children.Add(deviceName);
                chooser.Content = deviceRow;
                chooser.SetResourceReference(Control.BackgroundProperty, "MenuPanel");
                chooser.Visibility = Visibility.Collapsed;
                panel.Children.Add(chooser);
                panel.Children.Add(options);
                string label = Loc.Get(input ? "AudioInputLevel" : "StatusSound");
                slider = new Slider { Minimum = 0, Maximum = 100, SmallChange = 1, LargeChange = 5,
                    TickFrequency = 1, IsSnapToTickEnabled = true, IsMoveToPointEnabled = true,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,3,0,0) };
                slider.SetResourceReference(FrameworkElement.StyleProperty, "MenuVolume");
                Visuals.Identify(slider, input ? "StatusMenu.InputVolume" : "StatusMenu.Volume", label);
                slider.ValueChanged += delegate {
                    if (applying || selected == null) return;
                    changed();
                    int value = (int)Math.Round(slider.Value);
                    if (AudioEndpoints.SetVolume(selected.Id, value)) amount.Text = value + "%";
                    else LevelUnavailable();
                };
                levels.Children.Add(slider);
                string muteLabel = Loc.Get(input ? "AudioMuteInput" : "StatusMute");
                mute = new CheckBox { Content = StatusMenuChrome.Label(muteLabel, 13, "MenuText") };
                StatusMenuChrome.StyleSwitch(mute);
                Visuals.Identify(mute, input ? "StatusMenu.InputMute" : "StatusMenu.Mute", muteLabel);
                mute.SetResourceReference(Control.ForegroundProperty, "MenuText");
                RoutedEventHandler changeMute = delegate {
                    if (applying || selected == null) return;
                    changed();
                    if (!AudioEndpoints.SetMuted(selected.Id, mute.IsChecked == true)) LevelUnavailable();
                };
                mute.Checked += changeMute;
                mute.Unchecked += changeMute;
                levels.Children.Add(mute);
                panel.Children.Add(levels);
                state = Text(Loc.Get("AudioLoading"));
                Visuals.Identify(state, "StatusMenu." + prefix + "State", state.Text);
                panel.Children.Add(state);
            }

            private void LevelUnavailable()
            {
                levels.Visibility = Visibility.Collapsed;
                SetState(Loc.Get("AudioLevelUnavailable"));
                place();
            }

            private void SetState(string message)
            {
                state.Text = message;
                AutomationProperties.SetName(state, message);
                state.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            }

            internal void Update(AudioEndpoint[] next)
            {
                bool unavailable = next == null;
                if (unavailable) next = new AudioEndpoint[0];
                var current = next.FirstOrDefault(e => e.Current);
                // The timer must not re-target a thumb gesture if a different
                // default is picked in Windows before the mouse is released.
                if (slider.IsMouseCaptureWithin && selected != null && next.Any(e => e.Id == selected.Id)) return;
                bool optionsChanged = endpoints.Length != next.Length || !endpoints.Zip(next, (a,b) =>
                    a.Id == b.Id && a.Name == b.Name && a.Current == b.Current).All(equal => equal);
                endpoints = next; selected = current;
                applying = true;
                try {
                    chooser.Visibility = next.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                    string name = current == null ? Loc.Get("AudioChooseDevice") : current.Name;
                    deviceName.Text = name;
                    AutomationProperties.SetName(chooser, name);
                    AutomationProperties.SetItemStatus(chooser, current == null ? "" : current.Id);
                    if (next.Length == 0) options.Visibility = Visibility.Collapsed;
                    disclosure.Text = options.Visibility == Visibility.Visible ? "\uE70E" : "\uE70D";
                    if (optionsChanged && options.Visibility == Visibility.Visible) FillOptions();
                    bool levelReady = current != null && current.Volume >= 0;
                    levels.Visibility = levelReady ? Visibility.Visible : Visibility.Collapsed;
                    amount.Visibility = levelReady ? Visibility.Visible : Visibility.Collapsed;
                    Section.MinHeight = 0;
                    if (levelReady) {
                        slider.Value = current.Volume;
                        amount.Text = current.Volume + "%";
                        mute.IsChecked = current.Muted;
                    }
                    string message = unavailable ? "AudioUnavailable" : next.Length == 0 ? "AudioNo" + prefix :
                        selectionFailed ? "AudioSelectionFailed" : current == null ? "AudioNoDefault" :
                        !levelReady ? "AudioLevelUnavailable" : null;
                    SetState(message == null ? "" : Loc.Get(message));
                } finally { applying = false; }
            }

            private void ToggleOptions()
            {
                if (options.Visibility == Visibility.Visible) options.Visibility = Visibility.Collapsed;
                else { FillOptions(); options.Visibility = Visibility.Visible; }
                disclosure.Text = options.Visibility == Visibility.Visible ? "\uE70E" : "\uE70D";
                place();
            }

            private void FillOptions()
            {
                options.Children.Clear();
                foreach (var endpoint in endpoints) {
                    var option = Choice(prefix + "Option", endpoint.Name, delegate {
                        changed();
                        selectionFailed = !AudioEndpoints.Select(flow, endpoint.Id);
                        options.Visibility = Visibility.Collapsed;
                        disclosure.Text = "\uE70D";
                        if (!selectionFailed) {
                            // Until a fresh snapshot arrives the controls still
                            // contain the old device's level and mute state.
                            levels.Visibility = Visibility.Collapsed;
                            SetState(Loc.Get("AudioLoading"));
                        } else SetState(Loc.Get("AudioSelectionFailed"));
                        place();
                    });
                    AutomationProperties.SetItemStatus(option, endpoint.Id);
                    if (endpoint.Current) {
                        var line = new DockPanel();
                        var check = StatusMenuChrome.Glyph("\uE73E", 13, "MenuAccent");
                        check.Margin = new Thickness(8,0,0,0);
                        DockPanel.SetDock(check, Dock.Right);
                        line.Children.Add(check);
                        line.Children.Add(StatusMenuChrome.Label(endpoint.Name, 13, "MenuText"));
                        option.Content = line;
                        option.SetResourceReference(Control.BackgroundProperty, "MenuSelection");
                        AutomationProperties.SetHelpText(option, "current");
                    }
                    options.Children.Add(option);
                }
            }
        }
    }
}
