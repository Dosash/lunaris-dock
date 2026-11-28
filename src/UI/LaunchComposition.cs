using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Lunaris
{
    // DWM can transform the live backing surface of a layered WPF window. The
    // temporary presenter spans the button and the panel without resizing either.
    internal sealed class LaunchComposition : Forms.NativeWindow, IDisposable
    {
        internal const double SamplePhase = .25;
        private Device device;
        private Target target;
        private Visual visual;
        private Scale transform;
        private Effect effect;
        private readonly IntPtr source;
        private readonly Func<Point, Point?> finishAtPointer;
        private Rect hostBounds;
        private bool cloaked;
        internal LaunchComposition(IntPtr hwnd, Func<Point, Point?> finishAtPointer)
        {
            source = hwnd;
            this.finishAtPointer = finishAtPointer;
            try {
                CreateHandle(new Forms.CreateParams {
                    Style = unchecked((int)0x80000000), // WS_POPUP
                    ExStyle = 0x00200000 | 0x08000000 | 0x80, // NOREDIRECTIONBITMAP | NOACTIVATE | TOOLWINDOW
                    Width = 1, Height = 1
                });
                var iid = typeof(Device).GUID;
                DCompositionCreateDevice(IntPtr.Zero, ref iid, out device);
                device.CreateTargetForHwnd(Handle, true, out target);
                device.CreateVisual(out visual);
                device.CreateScaleTransform(out transform);
                device.CreateEffectGroup(out effect);
                object image;
                device.CreateSurfaceFromHwnd(source, out image);
                try { visual.SetContent(image); }
                finally { Release(image); }
                visual.SetTransform(transform);
                visual.SetEffect(effect);
                visual.SetBitmapInterpolationMode(1);
                target.SetRoot(visual);
            } catch { Dispose(); throw; }
        }

        internal void HideOriginal()
        {
            if (cloaked) return;
            SetCloak(source, true);
            cloaked = true;
        }

        private static void SetCloak(IntPtr hwnd, bool hide)
        {
            int value = hide ? 1 : 0;
            Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(hwnd, 13, ref value, sizeof(int))); // DWMWA_CLOAK
        }

        internal long Play(Rect panel, Rect button, IntPtr dock, Point centre, int frames, Func<double,double> ease,
            double from, double to, double alpha, double endAlpha)
        {
            hostBounds = Rect.Union(panel, button);
            var offset = new Point(panel.Left-hostBounds.Left, panel.Top-hostBounds.Top);
            visual.SetOffsetX((float)offset.X);
            visual.SetOffsetY((float)offset.Y);
            transform.SetCenterX((float)centre.X);
            transform.SetCenterY((float)centre.Y);
            Animation size = null, opacity = null;
            try {
                device.CreateAnimation(out size);
                device.CreateAnimation(out opacity);
                // Keep discontinuities away from the compositor's rounded refresh
                // timestamps. Interior samples still span exactly one 60 Hz period.
                for (int i = 0; i < frames; ++i) {
                    double t = ease(i / (double)frames);
                    double seconds = i == 0 ? 0 : (i + SamplePhase) / Program.AnimationFrameRate;
                    size.AddCubic(seconds, (float)(from+(to-from)*t), 0, 0, 0);
                    opacity.AddCubic(seconds, (float)(alpha+(endAlpha-alpha)*t), 0, 0, 0);
                }
                double end = (frames + SamplePhase) / Program.AnimationFrameRate;
                size.End(end, (float)to);
                opacity.End(end, (float)endAlpha);
                transform.AnimateX(size); transform.AnimateY(size);
                effect.AnimateOpacity(opacity);
                if (!cloaked) DwmFlush();
                device.Commit();
                if (!SetWindowPos(Handle, dock == IntPtr.Zero ? new IntPtr(-1) : dock, (int)hostBounds.X, (int)hostBounds.Y,
                    (int)Math.Ceiling(hostBounds.Width), (int)Math.Ceiling(hostBounds.Height), 0x0050)) // SHOWWINDOW | NOACTIVATE
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                HideOriginal();
                SetCloak(Handle, false);
                return Stopwatch.GetTimestamp();
            } finally { Release(opacity); Release(size); }
        }

        internal void Clear(bool handoff)
        {
            if (cloaked) {
                // Switch both surfaces just after a composition pass. Displaying
                // both for one refresh would darken the translucent menu.
                if (handoff) DwmFlush();
                if (Handle != IntPtr.Zero) SetCloak(Handle, true);
                SetCloak(source, false);
                cloaked = false;
            }
            if (Handle != IntPtr.Zero) ShowWindow(Handle, 0);
        }

        protected override void WndProc(ref Forms.Message message)
        {
            const int leftDown = 0x0201, rightDown = 0x0204, middleDown = 0x0207, wheel = 0x020A;
            if (message.Msg == leftDown || message.Msg == rightDown || message.Msg == middleDown || message.Msg == wheel) {
                long packed = message.LParam.ToInt64();
                var point = new Point(unchecked((short)packed), unchecked((short)(packed >> 16)));
                if (message.Msg != wheel) point.Offset(hostBounds.Left, hostBounds.Top);
                Point? client = finishAtPointer(point);
                if (client.HasValue) {
                    IntPtr position = message.Msg == wheel ? message.LParam :
                        new IntPtr(((int)client.Value.Y << 16) | ((int)client.Value.X & 0xffff));
                    PostMessage(source, message.Msg, message.WParam, position);
                }
                return;
            }
            base.WndProc(ref message);
        }

        public void Dispose()
        {
            try {
                if (cloaked) SetCloak(source, false);
            } catch (ExternalException ex) { Trace.WriteLine(ex); }
            finally {
                Release(target); target=null;
                Release(visual); visual=null;
                Release(transform); transform=null;
                Release(effect); effect=null;
                Release(device); device=null;
                if (Handle != IntPtr.Zero) DestroyHandle();
            }
        }

        private static void Release(object value) { if (value!=null) Marshal.ReleaseComObject(value); }
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmFlush();
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp);
        [DllImport("user32.dll", SetLastError=true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after,
            int x, int y, int width, int height, int flags);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("dcomp.dll", PreserveSig=false)] private static extern void DCompositionCreateDevice(IntPtr dxgi, ref Guid iid, out Device device);

        // Windows SDK dcomp.h and dcompanimation.h, in COM vtable order.
        [ComImport, Guid("C37EA93A-E7AA-450D-B16F-9746CB0407F3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface Device {
            void Commit(); void WaitForCommitCompletion(); void GetFrameStatistics();
            void CreateTargetForHwnd(IntPtr hwnd, bool topmost, out Target target);
            void CreateVisual(out Visual visual);
            void CreateSurface();
            void CreateVirtualSurface(); void CreateSurfaceFromHandle(); void CreateSurfaceFromHwnd(IntPtr hwnd, [MarshalAs(UnmanagedType.IUnknown)] out object image); void CreateTranslateTransform();
            void CreateScaleTransform(out Scale scale);
            void CreateRotateTransform(); void CreateSkewTransform(); void CreateMatrixTransform(); void CreateTransformGroup();
            void CreateTranslateTransform3D(); void CreateScaleTransform3D(); void CreateRotateTransform3D(); void CreateMatrixTransform3D();
            void CreateTransform3DGroup(); void CreateEffectGroup(out Effect effect); void CreateRectangleClip(); void CreateAnimation(out Animation animation);
        }
        [ComImport, Guid("EACDD04C-117E-4E17-88F4-D1B12B0E3D89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface Target { void SetRoot(Visual visual); }
        [ComImport, Guid("4D93059D-097B-4651-9A60-F0F25116E2F3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface Visual {
            void AnimateOffsetX(); void SetOffsetX(float x); void AnimateOffsetY(); void SetOffsetY(float y);
            void SetTransform(Scale transform); void SetMatrix(); void SetTransformParent(); void SetEffect(Effect effect);
            void SetBitmapInterpolationMode(int mode); void SetBorderMode(); void SetClip(); void SetClipRect();
            void SetContent([MarshalAs(UnmanagedType.IUnknown)] object content);
        }
        [ComImport, Guid("71FDE914-40EF-45EF-BD51-68B037C339F9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface Scale {
            void AnimateX(Animation value); void SetX(float x); void AnimateY(Animation value); void SetY(float y);
            void AnimateCenterX(); void SetCenterX(float x); void AnimateCenterY(); void SetCenterY(float y);
        }
        [ComImport, Guid("A7929A74-E6B2-4BD6-8B95-4040119CA34D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface Effect { void AnimateOpacity(Animation value); }
        [ComImport, Guid("CBFD91D9-51B2-45E4-B3DE-D19CCFB863C5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface Animation {
            void Reset(); void SetAbsoluteBeginTime();
            void AddCubic(double time, float constant, float linear, float quadratic, float cubic);
            void AddSinusoidal(); void AddRepeat(); void End(double time, float value);
        }
    }
}
