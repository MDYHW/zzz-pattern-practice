using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace VesperLab
{
    public sealed class ObservedKey
    {
        public string Key;
        public bool Down;
        public long Qpc;
        public uint OsTimeMs;
        public uint ReceivedOsTimeMs;
        public bool Injected;
        public IntPtr ForegroundWindow;
    }

    // Passive hooks only. Subscribers must enqueue and return immediately.
    public sealed class ObservedInput : IDisposable
    {
        private readonly Thread thread;
        private readonly ManualResetEvent started = new ManualResetEvent(false);
        private readonly HashSet<string> held = new HashSet<string>();
        private HookProc keyboardProc;
        private HookProc mouseProc;
        private IntPtr keyboardHook;
        private IntPtr mouseHook;
        private uint threadId;
        private Exception startupError;
        private volatile bool stopping;
        private int disposed;

        public event Action<ObservedKey> Received;

        public ObservedInput()
        {
            thread = new Thread(Run);
            thread.Name = "Lane passive input observer";
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!started.WaitOne(5000))
            {
                Dispose();
                throw new TimeoutException("Passive input hook installation did not complete within 5 seconds.");
            }
            if (startupError != null)
            {
                Dispose();
                throw new InvalidOperationException("Could not install passive input hooks.", startupError);
            }
        }

        private void Run()
        {
            try
            {
                threadId = GetCurrentThreadId();
                NativeMessage message;
                PeekMessage(out message, IntPtr.Zero, 0, 0, 0);
                keyboardProc = KeyboardCallback;
                mouseProc = MouseCallback;
                IntPtr module = GetModuleHandle(null);
                keyboardHook = SetWindowsHookEx(13, keyboardProc, module, 0);
                if (keyboardHook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Keyboard observation hook failed.");
                mouseHook = SetWindowsHookEx(14, mouseProc, module, 0);
                if (mouseHook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Mouse observation hook failed.");
                started.Set();
                if (!stopping) Application.Run(new ApplicationContext());
            }
            catch (Exception error)
            {
                startupError = error;
                started.Set();
            }
            finally
            {
                if (keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(keyboardHook);
                if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
                keyboardHook = IntPtr.Zero;
                mouseHook = IntPtr.Zero;
            }
        }

        private IntPtr KeyboardCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (code >= 0 && !stopping)
                {
                    long qpc = Stopwatch.GetTimestamp();
                    uint receivedMs = GetTickCount();
                    int message = wParam.ToInt32();
                    bool down = message == 0x0100 || message == 0x0104;
                    bool up = message == 0x0101 || message == 0x0105;
                    if (down || up)
                    {
                        KeyboardData data = (KeyboardData)Marshal.PtrToStructure(lParam, typeof(KeyboardData));
                        string key = data.VirtualKey == 0x20 ? "Space" : data.VirtualKey == 0x77 ? "F8" :
                            data.VirtualKey == 0x78 ? "F9" : data.VirtualKey == 0x1B ? "Escape" : null;
                        if (key != null && (data.Flags & 0x12) == 0)
                            Publish(key, down, qpc, data.Time, receivedMs);
                    }
                }
            }
            catch (Exception error) { Trace.WriteLine("Passive keyboard observer: " + error.Message); }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        private IntPtr MouseCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (code >= 0 && !stopping)
                {
                    long qpc = Stopwatch.GetTimestamp();
                    uint receivedMs = GetTickCount();
                    int message = wParam.ToInt32();
                    if (message == 0x0204 || message == 0x0205)
                    {
                        MouseData data = (MouseData)Marshal.PtrToStructure(lParam, typeof(MouseData));
                        if ((data.Flags & 0x03) == 0)
                            Publish("RMB", message == 0x0204, qpc, data.Time, receivedMs);
                    }
                }
            }
            catch (Exception error) { Trace.WriteLine("Passive mouse observer: " + error.Message); }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        private void Publish(string key, bool down, long qpc, uint osTimeMs, uint receivedMs)
        {
            if (down && !held.Add(key)) return;
            if (!down) held.Remove(key);
            Action<ObservedKey> callback = Received;
            if (callback != null)
                callback(new ObservedKey { Key = key, Down = down, Qpc = qpc, OsTimeMs = osTimeMs,
                    ReceivedOsTimeMs = receivedMs, Injected = false, ForegroundWindow = GetForegroundWindow() });
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            stopping = true;
            if (threadId != 0) PostThreadMessage(threadId, 0x0012, IntPtr.Zero, IntPtr.Zero);
            if (Thread.CurrentThread != thread && thread.Join(1500)) started.Close();
        }

        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardData
        {
            public uint VirtualKey, ScanCode, Flags, Time;
            public UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseData
        {
            public int X, Y;
            public uint MouseDataValue, Flags, Time;
            public UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMessage
        {
            public IntPtr Window;
            public uint Message;
            public UIntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public int X, Y;
            public uint Private;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int hook, HookProc callback, IntPtr module, uint thread);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostThreadMessage(uint thread, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint min, uint max, uint remove);
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll")]
        private static extern uint GetTickCount();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);
    }
}
