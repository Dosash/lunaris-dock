using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lunaris
{
    internal sealed partial class ShellCatalog
    {
        private sealed class Item
        {
            internal AppEntry App;
            internal string Target;
            internal byte[] ItemList;
        }

        private readonly object gate = new object();
        private Dictionary<string, Item> items = NewMap();
        private Dictionary<string, Item> targets = NewMap();
        private readonly Dictionary<string, AppEntry> processes =
            new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);

        private static Dictionary<string, Item> NewMap()
        {
            return new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
        }

        internal List<AppEntry> Read()
        {
            var next = NewMap();
            var nextTargets = NewMap();
            var shortcuts = NewMap();
            var list = new List<AppEntry>();
            dynamic shell = null, folder = null, children = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true));
                folder = shell.NameSpace("shell:AppsFolder");
                children = folder.Items();
                int count = children.Count;
                for (int i = 0; i < count; ++i)
                {
                    dynamic child = null;
                    try
                    {
                        child = children.Item(i);
                        string id = child.ExtendedProperty("System.AppUserModel.ID") as string;
                        if (String.IsNullOrWhiteSpace(id)) id = child.Path as string;
                        string name = child.Name as string;
                        if (String.IsNullOrWhiteSpace(id) || String.IsNullOrWhiteSpace(name) || next.ContainsKey(id)) continue;
                        string target = child.ExtendedProperty("System.Link.TargetParsingPath") as string;
                        var entry = new Item { App = new AppEntry { Id = id, Name = name }, Target = target };
                        IntPtr pidl;
                        if (SHGetIDListFromObject((object)child, out pidl) == 0 && pidl != IntPtr.Zero)
                        {
                            try
                            {
                                int length = checked((int)ILGetSize(pidl));
                                entry.ItemList = new byte[length];
                                Marshal.Copy(pidl, entry.ItemList, 0, length);
                                entry.App.Icon = GetIcon(pidl);
                            }
                            finally { Marshal.FreeCoTaskMem(pidl); }
                        }
                        if (entry.App.Icon == null) entry.App.Icon = FileIcon(target);
                        next.Add(id, entry);
                        string shortcut=BestShortcut((object)child);
                        if (shortcut!=null) shortcuts[shortcut]=entry;
                        if (IsExecutable(target) && !nextTargets.ContainsKey(target)) nextTargets.Add(target, entry);
                        list.Add(entry.App);
                    }
                    catch (COMException) { /* A shortcut can disappear during enumeration. */ }
                    finally { Release((object)child); }
                }
                var roots=new[] { Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                                  Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms) };
                ImportStartFolders((object)shell,shortcuts,next,nextTargets,list,roots);
            }
            finally
            {
                Release((object)children);
                Release((object)folder);
                Release((object)shell);
            }
            lock (gate)
            {
                items = next;
                targets = nextTargets;
                processes.Clear();
            }
            list.Sort((x,y) => StringComparer.CurrentCultureIgnoreCase.Compare(x.Name, y.Name));
            return list;
        }

        internal AppEntry Match(string windowId, string processId, string path)
        {
            lock (gate)
            {
                Item item;
                if ((!String.IsNullOrEmpty(windowId) && items.TryGetValue(windowId, out item)) ||
                    (!String.IsNullOrEmpty(processId) && items.TryGetValue(processId, out item)) ||
                    (!String.IsNullOrEmpty(path) && (targets.TryGetValue(path, out item) || items.TryGetValue(path, out item))))
                    return item.App;
                if (String.IsNullOrEmpty(path)) return null;
                AppEntry known;
                if (processes.TryGetValue(path, out known)) return known;
                string name = Path.GetFileNameWithoutExtension(path);
                try
                {
                    string description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                    if (!String.IsNullOrWhiteSpace(description)) name = description;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                known = new AppEntry { Id = path, Name = name, Icon = FileIcon(path) };
                processes[path] = known;
                return known;
            }
        }

        internal AppEntry Resolve(string id)
        {
            if (String.IsNullOrEmpty(id)) return null;
            lock (gate)
            {
                Item item;
                if (items.TryGetValue(id, out item)) return item.App;
            }
            return IsExecutable(id) && File.Exists(id) ? Match(null, null, id) : null;
        }

        internal void Launch(AppEntry app)
        {
            if (app == null || String.IsNullOrEmpty(app.Id)) return;
            Item item;
            lock (gate) items.TryGetValue(app.Id, out item);
            if (item != null && item.ItemList != null)
            {
                GCHandle pin = GCHandle.Alloc(item.ItemList, GCHandleType.Pinned);
                try
                {
                    var launch = new ExecuteInfo();
                    launch.Size = Marshal.SizeOf(typeof(ExecuteInfo));
                    launch.Mask = 0x00000004 | 0x00000400; // SEE_MASK_IDLIST | SEE_MASK_FLAG_NO_UI
                    launch.Show = 1;
                    launch.IdList = pin.AddrOfPinnedObject();
                    if (!ShellExecuteEx(ref launch)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    return;
                }
                finally { pin.Free(); }
            }
            if (IsExecutable(app.Id) && File.Exists(app.Id))
            {
                Process.Start(new ProcessStartInfo(app.Id) { UseShellExecute = true });
                return;
            }
            throw new InvalidOperationException("Приложение больше не доступно. Обновите список приложений.");
        }

        private static bool IsExecutable(string path)
        {
            return !String.IsNullOrEmpty(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                path.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathRooted(path);
        }

        private static void Release(object obj)
        {
            if (obj != null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj);
        }

        private static ImageSource GetIcon(IntPtr pidl)
        {
            Guid iid = typeof(IImageFactory).GUID;
            IImageFactory factory;
            if (SHCreateItemFromIDList(pidl, ref iid, out factory) != 0 || factory == null) return null;
            IntPtr bitmap = IntPtr.Zero;
            try
            {
                if (factory.GetImage(new SizeI { X = 128, Y = 128 }, 0x4, out bitmap) < 0 || bitmap == IntPtr.Zero) return null;
                NativeBitmap info;
                if (GetObject(bitmap, Marshal.SizeOf(typeof(NativeBitmap)), out info) == 0 ||
                    info.Width <= 0 || info.Height <= 0 || info.Width > 2048 || info.Height > 2048) return null;
                var header = new BitmapHeader { Size = 40, Width = info.Width, Height = -info.Height, Planes = 1, Bits = 32 };
                byte[] pixels = new byte[info.Width * info.Height * 4];
                IntPtr dc = GetDC(IntPtr.Zero);
                try { if (GetDIBits(dc, bitmap, 0, info.Height, pixels, ref header, 0) != info.Height) return null; }
                finally { ReleaseDC(IntPtr.Zero, dc); }
                var source = BitmapSource.Create(info.Width, info.Height, 96, 96,
                    NormalizeShellPixels(pixels), null, pixels, info.Width * 4);
                source.Freeze();
                return source;
            }
            finally
            {
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                Release(factory);
            }
        }

        private static PixelFormat NormalizeShellPixels(byte[] pixels)
        {
            // Shell providers supply both straight and premultiplied 32-bit DIBs.
            // A channel above its nonzero alpha proves the DIB is straight-alpha.
            bool hasAlpha=false, straight=false;
            for(int i=0;i<pixels.Length;i+=4) {
                int alpha=pixels[i+3];
                if(alpha==0) continue;
                hasAlpha=true;
                if(pixels[i]>alpha || pixels[i+1]>alpha || pixels[i+2]>alpha) { straight=true; break; }
            }
            if(!hasAlpha) return PixelFormats.Bgr32; // Legacy 32-bit RGB DIB with an unused alpha byte.
            for(int i=0;i<pixels.Length;i+=4) {
                int alpha=pixels[i+3];
                if(alpha==0) pixels[i]=pixels[i+1]=pixels[i+2]=0;
                else if(straight && alpha!=255)
                    for(int channel=0;channel<3;channel++) pixels[i+channel]=(byte)((pixels[i+channel]*alpha+127)/255);
            }
            return PixelFormats.Pbgra32;
        }

        private static ImageSource FileIcon(string path)
        {
            var info = new FileInfoNative();
            string location = String.IsNullOrWhiteSpace(path) ? "application.exe" : path;
            if (SHGetFileInfo(location, 0, ref info, Marshal.SizeOf(typeof(FileInfoNative)), 0x100) == IntPtr.Zero)
                SHGetFileInfo("application.exe", 0x80, ref info, Marshal.SizeOf(typeof(FileInfoNative)), 0x110);
            if (info.Icon == IntPtr.Zero) return null;
            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally { DestroyIcon(info.Icon); }
        }

        [StructLayout(LayoutKind.Sequential)] private struct SizeI { public int X, Y; }
        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IImageFactory
        {
            [PreserveSig] int GetImage(SizeI size, int flags, out IntPtr bitmap);
        }
        [StructLayout(LayoutKind.Sequential)] private struct NativeBitmap
        {
            public int Type, Width, Height, WidthBytes;
            public ushort Planes, Bits;
            public IntPtr Data;
        }
        [StructLayout(LayoutKind.Sequential)] private struct BitmapHeader
        {
            public int Size, Width, Height;
            public ushort Planes, Bits;
            public int Compression, SizeImage, XPels, YPels, Used, Important;
        }
        [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] private struct FileInfoNative
        {
            public IntPtr Icon;
            public int Index;
            public uint Attributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst=260)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst=80)] public string Type;
        }
        [StructLayout(LayoutKind.Sequential)] private struct ExecuteInfo
        {
            public int Size, Mask;
            public IntPtr Hwnd, Verb, File, Parameters, Directory;
            public int Show;
            public IntPtr Instance, IdList, Class, ClassKey;
            public int Hotkey;
            public IntPtr Icon, Process;
        }
        [DllImport("shell32.dll")] private static extern int SHGetIDListFromObject([MarshalAs(UnmanagedType.IUnknown)] object obj, out IntPtr pidl);
        [DllImport("shell32.dll")] private static extern uint ILGetSize(IntPtr pidl);
        [DllImport("shell32.dll")] private static extern int SHCreateItemFromIDList(IntPtr pidl, ref Guid iid, out IImageFactory factory);
        [DllImport("shell32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr SHGetFileInfo(string path, uint attr, ref FileInfoNative info, int size, uint flags);
        [DllImport("shell32.dll", EntryPoint="ShellExecuteExW", SetLastError=true)] private static extern bool ShellExecuteEx(ref ExecuteInfo info);
        [DllImport("gdi32.dll", EntryPoint="GetObjectW")] private static extern int GetObject(IntPtr obj, int bytes, out NativeBitmap data);
        [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, int start, int lines, [Out] byte[] bits, ref BitmapHeader header, uint usage);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    }
}
