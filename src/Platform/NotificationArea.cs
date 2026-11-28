using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lunaris
{
    internal static class NotificationArea
    {
        internal sealed class Item
        {
            internal string Key, Name;
            internal BitmapSource Icon;
            internal long Registration;
            internal uint ProcessId;
        }

        private sealed class RegisteredIcon
        {
            internal string Key, Name;
            internal IntPtr Owner;
            internal uint Process, Id, Message;
            internal int Version;
            internal long Registration;
            internal bool Hidden;
            internal Int32Rect Anchor;
            internal int AnchorTime;
            internal BitmapSource Source;
            internal readonly Dictionary<int, BitmapSource> Sizes = new Dictionary<int, BitmapSource>();

            internal BitmapSource AtSize(int pixels)
            {
                if (Source == null) return null;
                BitmapSource image;
                if (Sizes.TryGetValue(pixels, out image)) return image;
                double scale = pixels / (double)Math.Max(Source.PixelWidth, Source.PixelHeight);
                image = scale == 1 ? Source : new TransformedBitmap(Source, new ScaleTransform(scale, scale));
                image.Freeze();
                Sizes[pixels] = image;
                return image;
            }
        }

        private static readonly object gate = new object();
        private static readonly Dictionary<string, RegisteredIcon> icons = new Dictionary<string, RegisteredIcon>();
        private static long sequence;
        private static int openBars;
        private static TrayHost host;

        // One lease per expanded strip and one while replacement owns the tray.
        internal static bool UsesTrayHost { get { return openBars != 0 || (host != null && host.Selected); } }
        internal static void Open()
        {
            if (++openBars != 1) return;
            if (host == null) host = new TrayHost();
            host.SetOpen(true);
        }

        internal static void Close()
        {
            if (openBars == 0 || --openBars != 0) return;
            host.SetOpen(false);
        }

        internal static void RefreshOpenStrip()
        {
            if (host != null) host.RefreshSteamIcon();
        }

        internal static bool HasRegistration(uint pid)
        {
            lock (gate) return icons.Values.Any(i => i.Process == pid && i.Source != null && i.Message >= 0x400);
        }

        internal static void Reset()
        {
            lock (gate) icons.Clear();
        }

        internal static Item[] Read(int pixelSize)
        {
            if (host != null && host.Unavailable) throw new System.ComponentModel.Win32Exception(21);
            pixelSize = Math.Max(8, Math.Min(128, pixelSize));
            lock (gate) {
                var result = new List<Item>();
                foreach (var icon in icons.Values.OrderBy(i => i.Registration).ToArray()) {
                    uint pid;
                    if (GetWindowThreadProcessId(icon.Owner, out pid) == 0 || pid != icon.Process) {
                        icons.Remove(icon.Key);
                        continue;
                    }
                    if (icon.Hidden || icon.Source == null || icon.Message < 0x400) continue;
                    result.Add(new Item { Key = icon.Key, Registration = icon.Registration, ProcessId = icon.Process,
                        Name = String.IsNullOrWhiteSpace(icon.Name) ? Loc.Get("StatusApps") : icon.Name,
                        Icon = icon.AtSize(pixelSize) });
                }
                return result.ToArray();
            }
        }

        // Shell32 uses a packed, 32-bit-handle NOTIFYICONDATAW on the wire, even
        // for x64 clients. Both cbData and cbSize bound every optional field.
        private static string Key(IntPtr owner, uint id, Guid guid)
        {
            return guid != Guid.Empty ? guid.ToString("D") :
                owner.ToInt64().ToString("X",CultureInfo.InvariantCulture) + "/" + id.ToString(CultureInfo.InvariantCulture);
        }

        internal static bool Receive(IntPtr payload, int bytes, out int command, out uint ownerPid)
        {
            command = -1;
            ownerPid = 0;
            if (payload == IntPtr.Zero || bytes < 32 || bytes > 4096) return false;
            command = Marshal.ReadInt32(payload, 4);
            if (command < 0 || command > 4) return false;
            IntPtr data = IntPtr.Add(payload, 8);
            int length = Math.Min(bytes - 8, Marshal.ReadInt32(data));
            if (length < 24) return false;
            IntPtr owner = new IntPtr(Marshal.ReadInt32(data, 4));
            uint id = unchecked((uint)Marshal.ReadInt32(data, 8));
            int flags = Marshal.ReadInt32(data, 12);
            Guid guid = (flags & 0x20) != 0 && length >= 952 ?
                (Guid)Marshal.PtrToStructure(IntPtr.Add(data, 936), typeof(Guid)) : Guid.Empty;
            string key = Key(owner, id, guid);
            GetWindowThreadProcessId(owner, out ownerPid);

            lock (gate) {
                RegisteredIcon icon;
                icons.TryGetValue(key, out icon);
                if (command == 2) return icons.Remove(key);
                // A client asks its notification area to take focus after dismissing
                // its menu. Our strip is already open and never activates Explorer.
                if (command == 3) return icon != null;
                if (command == 4) {
                    if (icon != null && length >= 804) icon.Version = Marshal.ReadInt32(data, 800);
                    return icon != null;
                }
                if (ownerPid == 0) return false;
                if (command == 0 || icon == null || icon.Owner != owner || icon.Process != ownerPid) {
                    icon = new RegisteredIcon { Key = key, Id = id, Owner = owner, Process = ownerPid,
                        Registration = ++sequence };
                    icons[key] = icon;
                }
                if ((flags & 1) != 0) icon.Message = unchecked((uint)Marshal.ReadInt32(data, 16));
                if ((flags & 2) != 0) {
                    icon.Source = CopyImage(new IntPtr(Marshal.ReadInt32(data, 20)));
                    icon.Sizes.Clear();
                }
                if ((flags & 4) != 0) {
                    int characters = Math.Min(length <= 152 ? 64 : 128, (length - 24) / 2);
                    string tip = Marshal.PtrToStringUni(IntPtr.Add(data, 24), characters);
                    int terminator = tip.IndexOf('\0');
                    icon.Name = (terminator >= 0 ? tip.Substring(0, terminator) : tip).Trim();
                }
                if ((flags & 8) != 0 && length >= 288 && (Marshal.ReadInt32(data, 284) & 1) != 0)
                    icon.Hidden = (Marshal.ReadInt32(data, 280) & 1) != 0;
                return true;
            }
        }

        internal static bool GetAnchor(IntPtr payload, int bytes, out IntPtr result)
        {
            result = IntPtr.Zero;
            if (payload == IntPtr.Zero || bytes < 40 || Marshal.ReadInt32(payload, 8) < 32) return false;
            int part = Marshal.ReadInt32(payload, 4);
            if (part != 1 && part != 2) return false;
            IntPtr owner = new IntPtr(Marshal.ReadInt32(payload, 16));
            uint id = unchecked((uint)Marshal.ReadInt32(payload, 20));
            Guid guid = (Guid)Marshal.PtrToStructure(IntPtr.Add(payload, 24), typeof(Guid));
            lock (gate) {
                RegisteredIcon icon;
                if (!icons.TryGetValue(Key(owner, id, guid), out icon) || icon.Anchor.IsEmpty ||
                    unchecked((uint)(Environment.TickCount - icon.AnchorTime)) > 5000) return false;
                var rect = icon.Anchor;
                result = new IntPtr(part == 1 ? (rect.X & 0xffff) | (rect.Y << 16) :
                    (rect.Width & 0xffff) | (rect.Height << 16));
                return true;
            }
        }

        private static BitmapSource CopyImage(IntPtr icon)
        {
            if (icon == IntPtr.Zero) return null;
            try {
                // WIC interprets HICON's alpha/AND mask. Detach all pixels before
                // acknowledging the sender, which may immediately destroy its HICON.
                var source = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                if (source.PixelWidth > 1024 || source.PixelHeight > 1024) return null;
                var rgba = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
                int stride = rgba.PixelWidth * 4;
                var pixels = new byte[stride * rgba.PixelHeight];
                rgba.CopyPixels(pixels, stride, 0);
                var copy = BitmapSource.Create(rgba.PixelWidth, rgba.PixelHeight, 96, 96, rgba.Format, null, pixels, stride);
                copy.Freeze();
                return copy;
            } catch (ArgumentException) { return null; }
            catch (COMException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (System.ComponentModel.Win32Exception) { return null; }
        }

        internal static bool Activate(Item item, bool context, int screenX, int screenY)
        {
            return Activate(item, context, screenX, screenY, Int32Rect.Empty);
        }

        internal static bool Activate(Item item, bool context, int screenX, int screenY, Int32Rect bounds)
        {
            lock (gate) {
                RegisteredIcon icon;
                uint pid;
                if (item == null || !icons.TryGetValue(item.Key, out icon) || icon.Registration != item.Registration ||
                    icon.Hidden || icon.Message < 0x400 || icon.Message > 0xFFFF ||
                    GetWindowThreadProcessId(icon.Owner, out pid) == 0 || pid != icon.Process) return false;
                AllowSetForegroundWindow(pid);
                icon.Anchor = bounds;
                icon.AnchorTime = Environment.TickCount;
                bool version4 = icon.Version == 4;
                var w = new IntPtr(version4 ? ((screenX & 0xFFFF) | (screenY << 16)) : (long)icon.Id);
                int idBits = version4 ? (int)(icon.Id << 16) : 0;
                // Mouse callbacks are followed by the semantic select/context
                // notification for apps which opted into NOTIFYICON_VERSION or V4.
                int down = context ? 0x0204 : 0x0201;
                if (!PostMessage(icon.Owner, icon.Message, w, new IntPtr(idBits | down)) ||
                    !PostMessage(icon.Owner, icon.Message, w, new IntPtr(idBits | (down + 1)))) return false;
                return icon.Version < 3 || PostMessage(icon.Owner, icon.Message, w,
                    new IntPtr(idBits | (context ? 0x007B : 0x0400)));
            }
        }

        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint pid);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
    }
}
