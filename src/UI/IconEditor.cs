using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Lunaris
{
    internal sealed class IconEditor : Window, Forms.IWin32Window
    {
        private IconDesign draft;
        private bool reset;
        private readonly string name;
        private readonly Func<double,UIElement> original;
        private readonly List<Action> highlights = new List<Action>();
        private readonly Border preview = new Border(), dockPreview = new Border();
        private readonly StackPanel options = new StackPanel();
        private FrameworkElement glyphs, letters, imageAction;
        private TextBox baseColor, inkColor;
        private Button save;
        internal IconDesign Result { get { return reset ? null : draft; } }
        IntPtr Forms.IWin32Window.Handle { get { return new WindowInteropHelper(this).Handle; } }

        internal IconEditor(string name, IconDesign design, Func<double,UIElement> original)
        {
            this.name = name; this.original = original; draft = design.Copy();
            Title = Loc.Get("IconEditorTitle");
            Width = 640; Height = 568; WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false; Topmost = true; ResizeMode = ResizeMode.CanResize;
            Background = Appearance.Surface; Foreground = Appearance.Text; FontFamily = UiFonts.Text;
            UseLayoutRounding = true;
            Visuals.Identify(this,"IconEditor",Title);
            var layout = new Grid { Margin = new Thickness(18,14,18,12) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition());
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var heading = Label(name,22); heading.FontFamily = UiFonts.Display;
            heading.TextTrimming = TextTrimming.CharacterEllipsis;
            heading.Margin = new Thickness(0,0,0,14); layout.Children.Add(heading);

            var body = new Grid(); Grid.SetRow(body,1); layout.Children.Add(body);
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.95,GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2,GridUnitType.Star) });
            var stage = new Grid { Margin = new Thickness(8,12,8,12) };
            stage.RowDefinitions.Add(new RowDefinition());
            stage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            stage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            preview.VerticalAlignment = VerticalAlignment.Center;
            preview.HorizontalAlignment = HorizontalAlignment.Center;
            stage.Children.Add(preview);
            var dockLabel = Label(Loc.Get("IconDockPreview"),12);
            dockLabel.HorizontalAlignment = HorizontalAlignment.Center; dockLabel.Foreground = Appearance.Muted;
            Grid.SetRow(dockLabel,1); stage.Children.Add(dockLabel);
            dockPreview.HorizontalAlignment = HorizontalAlignment.Center;
            dockPreview.Margin = new Thickness(0,6,0,4);
            Grid.SetRow(dockPreview,2); stage.Children.Add(dockPreview);
            var card = new Border { Background = Appearance.Panel, BorderBrush = Appearance.Border, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(13), Child = stage, Margin = new Thickness(0,0,14,0), ClipToBounds = true };
            body.Children.Add(card);

            var scroll = new ScrollViewer { Content = options, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0,0,5,2) };
            Grid.SetColumn(scroll,1); body.Children.Add(scroll);
            var footer = new DockPanel { LastChildFill = false, Margin = new Thickness(0,14,0,0) };
            Grid.SetRow(footer,2); layout.Children.Add(footer);
            var restore = ActionButton("Reset",Loc.Get("IconOriginal"),delegate {
                reset = true;
                draft = IconDesign.Default(draft.Key,design.Shape == "folder");
                BuildOptions(); Refresh();
            });
            DockPanel.SetDock(restore,Dock.Left); footer.Children.Add(restore);
            save = ActionButton("Save",Loc.Get("Save"),delegate { DialogResult = true; });
            save.IsDefault = true; save.Background = Appearance.Selection;
            DockPanel.SetDock(save,Dock.Right); footer.Children.Add(save);
            var cancel = ActionButton("Cancel",Loc.Get("Cancel"),delegate { DialogResult = false; });
            cancel.IsCancel = true; DockPanel.SetDock(cancel,Dock.Right); footer.Children.Add(cancel);
            Content = layout;
            BuildOptions(); Refresh();
        }

        private static TextBlock Label(string caption, double size)
        {
            return new TextBlock { Text = caption, FontSize = size, FontFamily = UiFonts.Text, Foreground = Appearance.Text,
                VerticalAlignment = VerticalAlignment.Center };
        }

        private static Button ActionButton(string id, string caption, Action invoke)
        {
            var b = Visuals.Button("IconEditor."+id,caption);
            var text = Label(caption,13); text.HorizontalAlignment = HorizontalAlignment.Center;
            text.Margin = new Thickness(10,5,10,5); b.Content = text;
            b.Margin = new Thickness(2); b.Background = Appearance.Panel;
            b.Click += delegate { invoke(); };
            return b;
        }

        private void Heading(string caption)
        {
            var label = Label(caption,13);
            label.FontWeight = FontWeights.SemiBold;
            label.Margin = new Thickness(2,8,0,3); options.Children.Add(label);
        }

        private Button Choice(string id, string caption, Func<bool> selected, Action choose)
        {
            var b = ActionButton(id, caption, delegate { choose(); Changed(); });
            highlights.Add(delegate {
                b.Background = selected() ? Appearance.Selection : Appearance.Panel;
                System.Windows.Automation.AutomationProperties.SetItemStatus(b,selected() ? Loc.Get("Selected") : "");
            });
            return b;
        }

        private void BuildOptions()
        {
            options.Children.Clear(); highlights.Clear();
            Heading(Loc.Get("IconShape"));
            var shapes = new WrapPanel(); options.Children.Add(shapes);
            foreach (string shape in new[]{"folder","tile","none"})
                shapes.Children.Add(Choice("Shape."+shape,Loc.Get("IconShape."+shape),()=>draft.Shape==shape,()=>draft.Shape=shape));

            var colors = new UniformGrid { Rows=1, Columns=2, Margin=new Thickness(0,5,0,0) };
            options.Children.Add(colors);
            baseColor = ColorField(colors,"BaseColor",Loc.Get("IconBaseColor"),() => draft.BaseColor,value => draft.BaseColor=value);
            inkColor = ColorField(colors,"InkColor",Loc.Get("IconInkColor"),() => draft.InkColor,value => draft.InkColor=value);

            Heading(Loc.Get("IconForeground"));
            var modes = new WrapPanel(); options.Children.Add(modes);
            modes.Children.Add(Choice("Symbol",Loc.Get("IconSymbol"),()=>draft.Foreground=="symbol",()=>draft.Foreground="symbol"));
            modes.Children.Add(Choice("TextMode",Loc.Get("IconText"),()=>draft.Foreground=="text",()=>draft.Foreground="text"));
            var imageMode = ActionButton("ImageMode",Loc.Get("IconImage"),delegate {
                if (String.IsNullOrEmpty(draft.ImagePng)) Import(); else { draft.Foreground="image"; Changed(); }
            });
            highlights.Add(delegate { imageMode.Background = draft.Foreground=="image" ? Appearance.Selection : Appearance.Panel; });
            modes.Children.Add(imageMode);

            var gallery = new UniformGrid { Columns=8, Margin=new Thickness(0,3,0,1) };
            foreach (var key in IconGlyphs.Names)
            {
                string caption = Loc.Get("IconGlyph."+key);
                var button = Choice("Glyph."+key,caption,()=>draft.Glyph==key,()=>draft.Glyph=key);
                var drawing = IconDesign.Default("",false); drawing.Shape="none"; drawing.Glyph=key;
                drawing.Size=90; drawing.InkColor=Appearance.Text.Color.ToString().Substring(3).Insert(0,"#");
                var art = new Border { Child=drawing.Render(21), HorizontalAlignment=HorizontalAlignment.Center,
                    VerticalAlignment=VerticalAlignment.Center, Padding=new Thickness(0,3,0,3) };
                button.Content=art; gallery.Children.Add(button);
            }
            glyphs=gallery; options.Children.Add(glyphs);

            var input = Field("IconEditor.Text",Loc.Get("IconText"),draft.Text);
            input.Margin = new Thickness(2,5,2,5); input.MaxLength = 32;
            input.TextChanged += delegate { draft.Text=IconDesign.ShortText(input.Text); Changed(); };
            input.LostKeyboardFocus += delegate { if (input.Text!=draft.Text) input.Text=draft.Text; };
            letters=input; options.Children.Add(letters);

            imageAction=ActionButton("Import",Loc.Get("IconImport"),Import);
            options.Children.Add(imageAction);
            Heading(Loc.Get("IconPlacement"));
            AddSlider("Size",Loc.Get("IconSymbolSize"),20,100,draft.Size,n=>draft.Size=n);
            AddSlider("OffsetX",Loc.Get("IconHorizontal"),-35,35,draft.OffsetX,n=>draft.OffsetX=n);
            AddSlider("OffsetY",Loc.Get("IconVertical"),-35,35,draft.OffsetY,n=>draft.OffsetY=n);
        }

        private static TextBox Field(string id, string caption, string text)
        {
            var input = new TextBox { Text=text, Background=Appearance.Panel, Foreground=Appearance.Text,
                BorderBrush=Appearance.Border, CaretBrush=Appearance.Text, Padding=new Thickness(5,4,5,4),
                VerticalContentAlignment=VerticalAlignment.Center, FontFamily=UiFonts.Text };
            Visuals.Identify(input,id,caption); return input;
        }

        private TextBox ColorField(Panel parent,string id,string caption,Func<string> read,Action<string> write)
        {
            var stack=new StackPanel { Margin=new Thickness(2) }; parent.Children.Add(stack);
            stack.Children.Add(Label(caption,12));
            var row=new DockPanel { Margin=new Thickness(0,3,0,0) }; stack.Children.Add(row);
            var field=Field("IconEditor."+id,caption,read()); field.MaxLength=7;
            var pick=Visuals.Button("IconEditor.Pick"+id,Loc.Format("PickColor",caption));
            pick.Width=25; pick.Margin=new Thickness(0,0,3,0);
            pick.Background=Visuals.Brush(read()); DockPanel.SetDock(pick,Dock.Left);
            pick.Click+=delegate {
                Color c=Visuals.Brush(read()).Color;
                using (var chooser=new Forms.ColorDialog { FullOpen=true, Color=System.Drawing.Color.FromArgb(c.R,c.G,c.B) })
                    if (chooser.ShowDialog(this)==Forms.DialogResult.OK)
                        field.Text=String.Format("#{0:X2}{1:X2}{2:X2}",chooser.Color.R,chooser.Color.G,chooser.Color.B);
            };
            row.Children.Add(pick); row.Children.Add(field);
            field.TextChanged+=delegate {
                string color=Hex(field);
                if (color!=null) { write(color); pick.Background=Visuals.Brush(color); }
                field.BorderBrush=color==null ? Visuals.Brush("#CD624D") : Appearance.Border;
                Changed();
            };
            return field;
        }

        private static string Hex(TextBox field)
        {
            string value=field.Text.Trim();
            return UserSettings.NormalizeColor(value.Length==6 ? "#"+value : value,null);
        }

        private void AddSlider(string id,string caption,int min,int max,int initial,Action<int> change)
        {
            var row=new DockPanel { Margin=new Thickness(2,3,2,1) };
            var label=Label(caption,12); label.Width=97; DockPanel.SetDock(label,Dock.Left); row.Children.Add(label);
            var amount=Label(initial+"%",12); amount.Width=37; amount.TextAlignment=TextAlignment.Right;
            DockPanel.SetDock(amount,Dock.Right); row.Children.Add(amount);
            var slider=new Slider { Minimum=min,Maximum=max,Value=initial,SmallChange=1,TickFrequency=1,
                IsSnapToTickEnabled=true,VerticalAlignment=VerticalAlignment.Center,MinHeight=22 };
            Visuals.Identify(slider,"IconEditor."+id,caption);
            slider.ValueChanged+=delegate { int n=(int)Math.Round(slider.Value); change(n); amount.Text=n+"%"; Changed(); };
            row.Children.Add(slider); options.Children.Add(row);
        }

        private void Import()
        {
            var chooser=new OpenFileDialog { Title=Loc.Get("IconImport"),Filter="PNG / ICO (*.png;*.ico)|*.png;*.ico",CheckFileExists=true };
            if (chooser.ShowDialog(this)!=true) return;
            try
            {
                draft.ImagePng=IconDesign.ImportImage(chooser.FileName); draft.Foreground="image"; Changed();
            }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException ||
                    ex is FormatException || ex is ArgumentException || ex is System.Runtime.InteropServices.COMException)) throw;
                MessageBox.Show(this,Loc.Get("IconImportError"),Title,MessageBoxButton.OK,MessageBoxImage.Information);
            }
        }

        private void Changed() { reset=false; Refresh(); }

        private void Refresh()
        {
            foreach (var highlight in highlights) highlight();
            glyphs.Visibility=draft.Foreground=="symbol" ? Visibility.Visible : Visibility.Collapsed;
            letters.Visibility=draft.Foreground=="text" ? Visibility.Visible : Visibility.Collapsed;
            imageAction.Visibility=draft.Foreground=="image" ? Visibility.Visible : Visibility.Collapsed;
            preview.Child=reset ? original(122) : draft.Render(122);
            dockPreview.Child=reset ? original(40) : draft.Render(40);
            save.IsEnabled=reset || Hex(baseColor)!=null && Hex(inkColor)!=null &&
                (draft.Foreground!="text" || !String.IsNullOrWhiteSpace(draft.Text));
        }
    }
}
