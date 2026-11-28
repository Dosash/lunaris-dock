using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace Lunaris
{
    internal sealed class IconTarget
    {
        internal string Key, Name;
        internal bool Folder;
        internal Func<double, UIElement> Original;
    }

    public sealed partial class DesktopUi
    {
        private IconEditor iconEditor;
        internal bool IconEditorOpen { get { return iconEditor != null; } }
        internal bool EditingIcons { get; private set; }

        internal IconTarget IconFor(AppEntry app, LibraryItem folder, IEnumerable<LibraryItem> children)
        {
            if (folder != null) return new IconTarget { Key = folder.Id, Name = folder.Name, Folder = true,
                Original = size => Visuals.FolderIcon(size, folder, children, (child, s) => IconArt(IconFor(child,null,null), s)) };
            if (app != null) return new IconTarget { Key = LaunchLibrary.AppKey(app.Id), Name = app.Name,
                Folder = app.DirectoryPath != null, Original = size => Visuals.Icon(app, size) };
            return new IconTarget { Key = "lunaris:start", Name = Loc.Get("Start"), Original = Visuals.LaunchIcon };
        }

        private IconDesign DesignFor(string key) { return Settings.IconDesigns.FirstOrDefault(d => IdEqual(d.Key,key)); }

        internal UIElement IconArt(IconTarget target, double size)
        {
            var design = DesignFor(target.Key);
            return design == null ? target.Original(size) : design.Render(size);
        }

        internal void MarkIconButton(Button button, IconTarget target)
        {
            if (!EditingIcons) return;
            button.Cursor = Cursors.Pen;
            button.ToolTip = Visuals.Tip(Loc.Format("IconEditTarget", target.Name));
            AutomationProperties.SetHelpText(button, Loc.Format("IconEditTarget", target.Name));
        }

        internal MenuItem IconEditModeCommand()
        {
            var caption = Loc.Get(EditingIcons ? "IconFinishMode" : "IconStartMode");
            var item = Visuals.MenuItem(caption, delegate {
                EditingIcons = !EditingIcons;
                InvalidateDocks(); RenderDocks(false);
                if (launchpad != null && launchpad.IsOpen) launchpad.RefreshCatalog();
            });
            item.IsCheckable = true; item.IsChecked = EditingIcons;
            Visuals.Identify(item, "Icons.EditMode", caption);
            return item;
        }

        internal MenuItem IconEditCommand(IconTarget target, ScreenInfo screen)
        {
            string caption = Loc.Get("IconEdit");
            var item = Visuals.MenuItem(caption, delegate { EditIcon(target, screen); });
            Visuals.Identify(item, "Icons.Edit", caption);
            return item;
        }

        internal void EditIcon(IconTarget target, ScreenInfo screen)
        {
            if (iconEditor != null) { iconEditor.Activate(); return; }
            var existing = DesignFor(target.Key);
            var dialog = new IconEditor(target.Name, existing ?? IconDesign.Default(target.Key,target.Folder), target.Original);
            if (launchpad != null && launchpad.IsOpen) dialog.Owner = launchpad;
            if (screen != null)
            {
                var area = screen.WorkArea;
                double w = Math.Min(dialog.Width * screen.Scale, area.Width);
                double h = Math.Min(dialog.Height * screen.Scale, area.Height);
                dialog.WindowStartupLocation = WindowStartupLocation.Manual;
                dialog.SourceInitialized += delegate {
                    screen.Place(dialog, new Rect(area.Left+(area.Width-w)/2, area.Top+(area.Height-h)/2, w, h));
                };
            }
            iconEditor = dialog;
            try
            {
                if (dialog.ShowDialog() != true || disposed) return;
                Settings.IconDesigns.RemoveAll(d => IdEqual(d.Key,target.Key));
                if (dialog.Result != null) Settings.IconDesigns.Add(dialog.Result);
                Changed();
                if (launchpad != null && launchpad.IsOpen) launchpad.RefreshCatalog();
            }
            finally { iconEditor = null; }
        }
    }
}
