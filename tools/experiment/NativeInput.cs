using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace VesperLab
{
    public sealed class GameInputSink : IInputSink
    {
        private readonly IntPtr window;
        private readonly int pid;
        internal IntPtr WindowHandle { get { return window; } }
        public string ProcessName { get { return "ZenlessZoneZero"; } }
        public int ProcessId { get { return pid; } }
        public string WindowLabel { get; private set; }

        public GameInputSink()
        {
            window = Native.GetForegroundWindow();
            uint processId;
            Native.GetWindowThreadProcessId(window, out processId);
            pid = checked((int)processId);
            using (Process process = Process.GetProcessById(pid))
            {
                if (!String.Equals(process.ProcessName, ProcessName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("게임 창에서 F8을 눌러주세요. 현재 창에는 입력하지 않았습니다.");
            }
            var title = new StringBuilder(512);
            Native.GetWindowText(window, title, title.Capacity);
            WindowLabel = title.ToString();
        }

        public bool IsTargetForeground()
        {
            uint currentPid;
            IntPtr current = Native.GetForegroundWindow();
            Native.GetWindowThreadProcessId(current, out currentPid);
            return current == window && currentPid == pid;
        }
        public bool AnyControlHeld(string ownedKey = null, bool includeLmb = false, bool includeE = false)
        {
            foreach (int key in new[] { 0x20, 0x02, 0x01, 0x45, 0x10, 0x11, 0x12, 0x5B, 0x5C })
            {
                if (key == 0x01 && !includeLmb) continue;
                if (key == 0x45 && !includeE) continue;
                if ((ownedKey == "RMB" && key == 0x02) || (ownedKey == "Space" && key == 0x20)
                    || (ownedKey == "LMB" && key == 0x01) || (ownedKey == "E" && key == 0x45)) continue;
                if ((Native.GetAsyncKeyState(key) & 0x8000) != 0) return true;
            }
            return false;
        }
        public bool AnyDirectionHeld(string ownedKey = null)
        {
            foreach (int key in new[] { 0x57, 0x41, 0x53, 0x44 })
            {
                if ((ownedKey == "A" && key == 0x41) || (ownedKey == "D" && key == 0x44)) continue;
                if ((Native.GetAsyncKeyState(key) & 0x8000) != 0) return true;
            }
            return false;
        }
        public int Send(string key, bool down, out int error)
        {
            var input = new Native.INPUT();
            if (key == "Space" || key == "A" || key == "D" || key == "E")
            {
                input.type = 1;
                input.data.keyboard.wScan = (ushort)(key == "Space" ? 0x39 : key == "A" ? 0x1e : key == "D" ? 0x20 : 0x12);
                input.data.keyboard.dwFlags = 0x0008u | (down ? 0u : 0x0002u);
            }
            else if (key == "RMB" || key == "LMB")
            {
                input.type = 0;
                input.data.mouse.dwFlags = key == "LMB" ? (down ? 0x0002u : 0x0004u) : (down ? 0x0008u : 0x0010u);
            }
            else throw new ArgumentException("Unknown key", "key");
            int sent = (int)Native.SendInput(1, new[] { input }, Marshal.SizeOf(typeof(Native.INPUT)));
            error = sent == 1 ? 0 : Marshal.GetLastWin32Error();
            return sent;
        }
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; }
        [DllImport("user32.dll")] internal static extern bool GetClientRect(IntPtr window, out RECT rect);
        [DllImport("user32.dll")] internal static extern bool ClientToScreen(IntPtr window, ref POINT point);
        [StructLayout(LayoutKind.Sequential)] internal struct INPUT { public uint type; public InputUnion data; }
        [StructLayout(LayoutKind.Explicit)] internal struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mouse;
            [FieldOffset(0)] public KEYBDINPUT keyboard;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct MOUSEINPUT
        {
            public int dx, dy; public uint mouseData, dwFlags, time; public UIntPtr dwExtraInfo;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct KEYBDINPUT
        {
            public ushort wVk, wScan; public uint dwFlags, time; public UIntPtr dwExtraInfo;
        }
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr window, StringBuilder text, int length);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr window, int id);
    }
}
