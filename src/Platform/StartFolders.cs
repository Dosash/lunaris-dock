using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Lunaris
{
    internal sealed partial class ShellCatalog
    {
        private static string BestShortcut(object child)
        {
            byte[] pidl;
            try { pidl=((dynamic)child).ExtendedProperty("System.AppUserModel.BestShortcut") as byte[]; }
            catch(COMException) { return null; }
            if (pidl==null) return null;
            // The AppsFolder property contains a serialized absolute PIDL. Verify its
            // terminating SHITEMID before handing the byte buffer to the native parser.
            int pos=0;
            while (pos+2<=pidl.Length)
            {
                int size=pidl[pos] | (pidl[pos+1]<<8);
                if (size==0)
                {
                    var path=new StringBuilder(32768);
                    return SHGetPathFromIDListEx(pidl,path,(uint)path.Capacity,0) ? path.ToString() : null;
                }
                if (size<2 || size>pidl.Length-pos) break;
                pos+=size;
            }
            return null;
        }

        private static void ImportStartFolders(object shell, Dictionary<string,Item> shortcuts, Dictionary<string,Item> next,
                                              Dictionary<string,Item> targets, List<AppEntry> apps, string[] roots)
        {
            var labels=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            var files=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var otherLinks=new List<KeyValuePair<string,string>>();
            // Programs folders merge like the native Start menu. An identically named
            // per-user shortcut wins. Separate shortcut files can have distinct arguments.
            foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(root)) continue;
                var pending=new Queue<string>(); pending.Enqueue("");
                while (pending.Count!=0)
                {
                    string relative=pending.Dequeue(), directory=Path.Combine(root,relative);
                    dynamic folder=null;
                    try
                    {
                        folder=((dynamic)shell).NameSpace(directory);
                        string existing, raw=Path.GetFileName(relative);
                        if (relative.Length>0 && (!labels.TryGetValue(relative,out existing) || existing==raw))
                        {
                            string display=raw;
                            if (folder!=null)
                            {
                                dynamic self=folder.Self;
                                try { display=self.Name as string ?? display; }
                                finally { Release((object)self); }
                            }
                            labels[relative]=display;
                        }
                        foreach (var path in Directory.GetFileSystemEntries(directory))
                        {
                            try
                            {
                                var attrs=File.GetAttributes(path);
                                if ((attrs & (FileAttributes.ReparsePoint|FileAttributes.Hidden))!=0) continue;
                                string name=Path.GetFileName(path), child=Path.Combine(relative,name);
                                if ((attrs & FileAttributes.Directory)!=0) { pending.Enqueue(child); continue; }
                                string ext=Path.GetExtension(name);
                                if (!String.Equals(ext,".lnk",StringComparison.OrdinalIgnoreCase) &&
                                    !String.Equals(ext,".url",StringComparison.OrdinalIgnoreCase) &&
                                    !String.Equals(ext,".appref-ms",StringComparison.OrdinalIgnoreCase)) continue;
                                if (!files.Add(child)) continue;
                                Item source;
                                if (shortcuts.TryGetValue(path,out source)) { source.App.StartFolder=relative; continue; }
                                otherLinks.Add(new KeyValuePair<string,string>(path,child));
                            }
                            catch (COMException) { }
                            catch (IOException) { }
                            catch (UnauthorizedAccessException) { }
                        }
                    }
                    catch (COMException) { }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    finally { Release((object)folder); }
                }
            }
            // Resolve aliases after every selected BestShortcut has its authoritative
            // folder. A shadowed common shortcut must not claim a slot in the tree.
            foreach (var group in otherLinks.GroupBy(link=>Path.GetDirectoryName(link.Key),StringComparer.OrdinalIgnoreCase))
            {
                object parent=null;
                try
                {
                    parent=((dynamic)shell).NameSpace(group.Key);
                    if (parent==null) continue;
                    foreach (var file in group)
                    {
                        try { ImportLink(parent,Path.GetFileName(file.Key),file.Value,Path.GetDirectoryName(file.Value) ?? "",next,targets,apps); }
                        catch (COMException) { }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }
                catch (COMException) { }
                finally { Release(parent); }
            }
            foreach (var app in apps) app.StartFolderNames=labels;
        }

        private static void ImportLink(object parent,string filename,string relative,string folder,
                                       Dictionary<string,Item> next,Dictionary<string,Item> targets,List<AppEntry> apps)
        {
            dynamic link=((dynamic)parent).ParseName(filename);
            if (link==null) return;
            try
            {
                string name=link.Name as string;
                if (String.IsNullOrWhiteSpace(name)) name=Path.GetFileNameWithoutExtension(filename);
                string modelId=link.ExtendedProperty("System.AppUserModel.ID") as string;
                string target=link.ExtendedProperty("System.Link.TargetParsingPath") as string;
                Item identified=null;
                bool matches=(!String.IsNullOrWhiteSpace(modelId) && next.TryGetValue(modelId,out identified)) ||
                             (!String.IsNullOrWhiteSpace(target) && targets.TryGetValue(target,out identified) &&
                              String.Equals(identified.App.Name,name,StringComparison.CurrentCultureIgnoreCase));
                IntPtr pidl;
                if (SHGetIDListFromObject((object)link,out pidl)!=0 || pidl==IntPtr.Zero) return;
                try
                {
                    byte[] data=new byte[checked((int)ILGetSize(pidl))];
                    Marshal.Copy(pidl,data,0,data.Length);
                    if (matches && identified.App.StartFolder==null)
                    {
                        identified.App.StartFolder=folder;
                        identified.ItemList=data; // Keep the preferred shortcut's arguments.
                        return;
                    }
                    string id="startlink:"+relative;
                    if(next.ContainsKey(id)) return;
                    var app=new AppEntry { Id=id,Name=name,StartFolder=folder,Icon=GetIcon(pidl) ?? FileIcon(target) };
                    var item=new Item { App=app,ItemList=data,Target=target };
                    next.Add(id,item); apps.Add(app);
                    if (IsExecutable(target) && !targets.ContainsKey(target)) targets.Add(target,item);
                }
                finally { Marshal.FreeCoTaskMem(pidl); }
            }
            finally { Release((object)link); }
        }
        [DllImport("shell32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]
        private static extern bool SHGetPathFromIDListEx([In] byte[] pidl,StringBuilder path,uint capacity,uint flags);
    }
}
