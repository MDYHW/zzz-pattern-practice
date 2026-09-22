using System;
using System.Runtime.InteropServices;
using System.Drawing;

namespace VesperLab
{
    // Read-only window APIs. This executable deliberately has no input injection API.
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; }
        [DllImport("user32.dll")] internal static extern bool GetClientRect(IntPtr window, out RECT rect);
        [DllImport("user32.dll")] internal static extern bool ClientToScreen(IntPtr window, ref POINT point);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        internal static Rectangle ClientBounds(IntPtr window)
        {
            RECT r; var p = new POINT();
            if (!GetClientRect(window, out r) || !ClientToScreen(window, ref p)) throw new InvalidOperationException("게임 창 위치를 읽지 못했습니다.");
            return new Rectangle(p.X, p.Y, r.Right - r.Left, r.Bottom - r.Top);
        }
    }
}
