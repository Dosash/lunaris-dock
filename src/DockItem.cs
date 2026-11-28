using System;
using System.Collections.Generic;
using System.IO;

namespace Lunaris
{
    // App pins keep their original shell ID or executable path in settings.json.
    internal sealed class DockItem
    {
        internal const string SeparatorPrefix = "dock-separator:";
        internal const string FolderPrefix = "dock-folder:";
        internal const string DirectoryPrefix = "dock-directory:";

        internal string Key;
        internal AppEntry App;
        internal LibraryItem Folder;
        internal List<LibraryItem> Children;
        internal bool IsSeparator { get { return Key.StartsWith(SeparatorPrefix, StringComparison.OrdinalIgnoreCase); } }
        internal string Name { get { return App != null ? App.Name : Folder != null ? Folder.Name : Loc.Get("DockSeparator"); } }

        internal static string DirectoryKey(string path)
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full);
            return DirectoryPrefix + (full.Length == root.Length ? root : full.TrimEnd(Path.DirectorySeparatorChar));
        }

        internal static AppEntry DirectoryApp(string key)
        {
            string path = key.Substring(DirectoryPrefix.Length);
            if (!ShellCatalog.AbsolutePath(path) || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
            string name = Path.GetFileName(path);
            return new AppEntry { Id = key, Name = String.IsNullOrEmpty(name) ? path : name, DirectoryPath = path };
        }
    }
}
