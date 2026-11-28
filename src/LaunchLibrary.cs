using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace Lunaris
{
    [DataContract]
    public sealed class LibrarySettings
    {
        [DataMember] public List<LibraryFolder> Folders = new List<LibraryFolder>();
        [DataMember] public List<LibraryPlacement> Placements = new List<LibraryPlacement>();
        [DataMember] public List<LibraryOrder> Orders = new List<LibraryOrder>();

        internal void Normalize()
        {
            var eq = StringComparer.OrdinalIgnoreCase;
            Folders = (Folders ?? new List<LibraryFolder>()).Where(f => f != null && !String.IsNullOrWhiteSpace(f.Name) &&
                f.Id != null && f.Id.StartsWith("user:",StringComparison.OrdinalIgnoreCase))
                .GroupBy(f => f.Id,eq).Select(g => g.First()).ToList();
            Placements = (Placements ?? new List<LibraryPlacement>()).Where(p => p != null &&
                !String.IsNullOrEmpty(p.Id) && p.Parent != null).GroupBy(p => p.Id,eq).Select(g => g.Last()).ToList();
            Orders = (Orders ?? new List<LibraryOrder>()).Where(o => o != null && o.Parent != null)
                .GroupBy(o => o.Parent,eq).Select(g => g.Last()).ToList();
            foreach (var entry in Orders)
                entry.Items = (entry.Items ?? new List<string>()).Where(id => !String.IsNullOrEmpty(id)).Distinct(eq).ToList();
        }
    }
    [DataContract] public sealed class LibraryFolder
    {
        [DataMember] public string Id;
        [DataMember] public string Name;
    }
    [DataContract] public sealed class LibraryPlacement
    {
        [DataMember] public string Id;
        [DataMember] public string Parent;
    }
    [DataContract] public sealed class LibraryOrder
    {
        [DataMember] public string Parent;
        [DataMember] public List<string> Items;
    }

    public sealed class LibraryItem
    {
        public string Id { get; internal set; }
        public string Name { get; internal set; }
        public string ParentId { get; internal set; }
        public AppEntry App { get; internal set; }
        public bool IsFolder { get { return App == null; } }
        public bool IsSystem { get; internal set; }
    }

    public sealed class LaunchLibrary
    {
        public const string Root = "";
        private static readonly StringComparer Eq = StringComparer.OrdinalIgnoreCase;
        private readonly LibrarySettings saved;
        private readonly Dictionary<string,LibraryItem> entries = new Dictionary<string,LibraryItem>(Eq);

        public LaunchLibrary(UserSettings settings, IEnumerable<AppEntry> apps)
        {
            if (settings.Library == null) settings.Library = new LibrarySettings();
            saved = settings.Library;
            saved.Normalize();
            var catalog = apps.Where(a => a != null && !String.IsNullOrWhiteSpace(a.Id)).ToList();
            var names = catalog.Select(a => a.StartFolderNames).FirstOrDefault(n => n != null);
            foreach (var app in catalog)
            {
                string id = AppKey(app.Id);
                if (!entries.ContainsKey(id)) entries.Add(id,new LibraryItem { Id=id, Name=app.Name, App=app,
                    ParentId=SystemFolder(app.StartFolder,names) });
            }
            foreach (var f in saved.Folders)
                entries[f.Id] = new LibraryItem { Id=f.Id, Name=f.Name, ParentId=Root };
            // A native folder with no launchable descendants is hidden on first use.
            // Keep a physical directory the user has organized, even when its last
            // shortcut was uninstalled. Never recreate a directory removed by Windows.
            if (names != null)
            {
                var pinnedFolders = (settings.Pins ?? Enumerable.Empty<string>())
                    .Where(key => key != null && key.StartsWith(DockItem.FolderPrefix, StringComparison.OrdinalIgnoreCase))
                    .Select(key => key.Substring(DockItem.FolderPrefix.Length));
                foreach (string key in saved.Placements.SelectMany(p => new[]{p.Id,p.Parent}).Concat(pinnedFolders).Distinct(Eq))
                {
                    if (!key.StartsWith("start:",StringComparison.OrdinalIgnoreCase)) continue;
                    string path=key.Substring("start:".Length);
                    if (names.ContainsKey(path)) SystemFolder(path,names);
                }
            }
            foreach (var place in saved.Placements)
            {
                var item = Find(place.Id);
                if (item != null && IsParent(place.Parent) && !Eq.Equals(item.Id,place.Parent)) item.ParentId=place.Parent;
            }
            // Apply the complete parent graph before checking it. A real folder can be
            // promoted and become the parent of its former ancestor in a later edit.
            foreach (var folder in entries.Values.Where(i=>i.IsFolder))
            {
                var seen=new HashSet<string>(Eq);
                for (var node=folder;node!=null;node=Find(node.ParentId))
                {
                    if (seen.Add(node.Id)) continue;
                    node.ParentId=Root;
                    break;
                }
            }
        }

        public static string AppKey(string shellAppId) { return "app:" + shellAppId; }

        private string SystemFolder(string relative, Dictionary<string,string> names)
        {
            if (String.IsNullOrEmpty(relative)) return Root;
            string parent = Root, path = "";
            foreach (string part in relative.Split(new[]{'\\','/'},StringSplitOptions.RemoveEmptyEntries))
            {
                path = path.Length == 0 ? part : path + "\\" + part;
                string id = "start:" + path;
                if (!entries.ContainsKey(id))
                {
                    string name;
                    if (names == null || !names.TryGetValue(path,out name) || String.IsNullOrEmpty(name)) name=part;
                    entries.Add(id,new LibraryItem { Id=id,Name=name,ParentId=parent,IsSystem=true });
                }
                parent=id;
            }
            return parent;
        }

        public LibraryItem Find(string id)
        {
            LibraryItem item;
            return id != null && entries.TryGetValue(id,out item) ? item : null;
        }

        public List<LibraryItem> Folders()
        {
            return entries.Values.Where(item => item.IsFolder)
                .OrderBy(item => item.Name,StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.Id,Eq).ToList();
        }

        public List<LibraryItem> Children(string folderId)
        {
            var order = Order(folderId);
            var ranks = new Dictionary<string,int>(Eq);
            if (order != null) foreach (string id in order.Items) ranks[id]=ranks.Count;
            return entries.Values.Where(item => Eq.Equals(item.ParentId,folderId))
                .OrderBy(item => { int index; return ranks.TryGetValue(item.Id,out index) ? index : Int32.MaxValue; })
                .ThenByDescending(item => item.IsFolder).ThenBy(item => item.Name,StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Id,Eq).ToList();
        }

        private bool IsParent(string id) { var item=Find(id); return id==Root || (item!=null && item.IsFolder); }
        private bool CanParent(LibraryItem item, string id)
        {
            if (!IsParent(id)) return false;
            if (item.IsFolder)
                for (var ancestor=Find(id); ancestor!=null; ancestor=Find(ancestor.ParentId))
                    if (Eq.Equals(ancestor.Id,item.Id)) return false;
            return true;
        }
        private LibraryOrder Order(string folderId) { return saved.Orders.FirstOrDefault(o => Eq.Equals(o.Parent,folderId)); }
        public bool ManualOrder(string folderId) { return Order(folderId)!=null; }
        public bool SetManualOrder(string folderId)
        {
            if (!IsParent(folderId) || ManualOrder(folderId)) return false;
            saved.Orders.Add(new LibraryOrder { Parent=folderId,Items=Children(folderId).Select(i=>i.Id).ToList() });
            return true;
        }
        public bool SortByName(string folderId) { return saved.Orders.RemoveAll(o=>Eq.Equals(o.Parent,folderId))!=0; }

        private void Place(LibraryItem item,string parent)
        {
            saved.Placements.RemoveAll(p=>Eq.Equals(p.Id,item.Id));
            saved.Placements.Add(new LibraryPlacement { Id=item.Id,Parent=parent });
            item.ParentId=parent;
        }
        private bool NameAvailable(string name, string parent, string except)
        {
            return !String.IsNullOrWhiteSpace(name) && !entries.Values.Any(i=>i.IsFolder && Eq.Equals(i.ParentId,parent) &&
                !Eq.Equals(i.Id,except) && StringComparer.CurrentCultureIgnoreCase.Equals(i.Name,name.Trim()));
        }

        public string CreateFolder(string name,string parentId)
        {
            if (!IsParent(parentId) || !NameAvailable(name,parentId,null)) return null;
            string id="user:"+Guid.NewGuid().ToString("N");
            var folder=new LibraryFolder { Id=id,Name=name.Trim() };
            saved.Folders.Add(folder);
            var item=new LibraryItem { Id=id,Name=folder.Name,ParentId=Root };
            entries.Add(id,item);
            if (parentId!=Root) Place(item,parentId);
            var order=Order(parentId);
            if (order!=null) order.Items.Add(id);
            return id;
        }

        public bool RenameFolder(string id,string name)
        {
            var folder=saved.Folders.FirstOrDefault(f=>Eq.Equals(f.Id,id));
            var item=Find(id);
            if (folder==null || !NameAvailable(name,item.ParentId,id) || folder.Name==name.Trim()) return false;
            folder.Name=name.Trim(); item.Name=folder.Name; return true;
        }

        public bool Move(string id,string parentId,string beforeId=null)
        {
            var item=Find(id);
            var before=Find(beforeId);
            if (item==null || !CanParent(item,parentId) || Eq.Equals(id,beforeId) ||
                (beforeId!=null && (before==null || !Eq.Equals(before.ParentId,parentId)))) return false;
            // Materialize newly installed items in current visual order. Keep records for
            // temporarily unavailable programs so offline/reinstalled apps retain their slots.
            SetManualOrder(parentId);
            var dest=Order(parentId).Items;
            var known=new HashSet<string>(dest,Eq);
            foreach (var child in Children(parentId)) if(known.Add(child.Id)) dest.Add(child.Id);
            if (!Eq.Equals(item.ParentId,parentId))
            {
                var old=Order(item.ParentId);
                if (old!=null) old.Items.RemoveAll(v=>Eq.Equals(v,id));
                Place(item,parentId);
            }
            dest.RemoveAll(v=>Eq.Equals(v,id));
            int index=beforeId==null ? -1 : dest.FindIndex(v=>Eq.Equals(v,beforeId));
            dest.Insert(index<0?dest.Count:index,id);
            return true;
        }

        public bool DeleteFolder(string id)
        {
            var item=Find(id);
            if (item==null || !item.IsFolder || item.IsSystem) return false;
            var children=Children(id);
            string parent=item.ParentId;
            var order=Order(parent);
            if (order!=null)
            {
                var childOrder=Order(id);
                var members=(childOrder==null ? new List<string>() : childOrder.Items)
                    .Concat(children.Select(c=>c.Id)).Distinct(Eq).ToList();
                int place=order.Items.FindIndex(v=>Eq.Equals(v,id));
                order.Items.RemoveAll(v=>Eq.Equals(v,id) || members.Contains(v,Eq));
                order.Items.InsertRange(Math.Min(place<0?order.Items.Count:place,order.Items.Count),members);
            }
            foreach(var child in children) Place(child,parent);
            foreach(var missing in saved.Placements.Where(p=>Eq.Equals(p.Parent,id))) missing.Parent=parent;
            saved.Folders.RemoveAll(f=>Eq.Equals(f.Id,id));
            saved.Placements.RemoveAll(p=>Eq.Equals(p.Id,id));
            saved.Orders.RemoveAll(o=>Eq.Equals(o.Parent,id));
            entries.Remove(id);
            return true;
        }
    }
}
