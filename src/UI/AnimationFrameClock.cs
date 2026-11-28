using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;

namespace Lunaris
{
    // Layered WPF windows can receive several Rendering events together on a
    // high-refresh display. Pace the transform updates themselves, with at most
    // one outstanding dispatcher operation and no timer running while idle.
    internal sealed class AnimationFrameClock
    {
        private volatile bool stopped;

        internal AnimationFrameClock(Dispatcher dispatcher, Action<double> frame)
        {
            ThreadPool.QueueUserWorkItem(delegate {
                using (var timer = new FrameTimer()) {
                    long period = (Stopwatch.Frequency + Program.AnimationFrameRate - 1) / Program.AnimationFrameRate;
                    long start = Stopwatch.GetTimestamp(), previous = start;
                    while (!stopped) {
                        long remaining = previous + period - Stopwatch.GetTimestamp();
                        if (remaining > 0) { timer.Wait(remaining); continue; }
                        if (dispatcher.HasShutdownStarted) return;
                        try {
                            dispatcher.Invoke(DispatcherPriority.Render, new Action(delegate {
                                if (stopped) return;
                                previous = Stopwatch.GetTimestamp();
                                frame((previous - start) / (double)Stopwatch.Frequency);
                            }));
                        } catch (OperationCanceledException) { return; }
                    }
                }
            });
        }

        internal void Stop() { stopped = true; }

        private sealed class FrameTimer : WaitHandle
        {
            internal FrameTimer()
            {
                SafeWaitHandle = CreateWaitableTimerEx(IntPtr.Zero, null, 2, 0x00100002);
            }

            internal void Wait(long ticks)
            {
                long due = -Math.Max(1, ticks * TimeSpan.TicksPerSecond / Stopwatch.Frequency);
                if (!SafeWaitHandle.IsInvalid && SetWaitableTimer(SafeWaitHandle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                    WaitOne();
                else
                    Thread.Sleep((int)Math.Ceiling(-due / (double)TimeSpan.TicksPerMillisecond));
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern SafeWaitHandle CreateWaitableTimerEx(IntPtr attributes, string name, uint flags, uint access);
        [DllImport("kernel32.dll")]
        private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long due, int period, IntPtr callback, IntPtr arg, bool resume);
    }
}
