using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace HoverVideoPreview
{
    internal static class Native
    {
        internal const int WM_HOTKEY = 0x0312;
        internal const int WS_EX_TOOLWINDOW = 0x80;
        internal const int WS_EX_NOACTIVATE = 0x08000000;
        internal const uint SWP_NOSIZE = 0x0001;
        internal const uint SWP_NOMOVE = 0x0002;
        internal const uint SWP_NOACTIVATE = 0x0010;
        internal const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_NOREPEAT = 0x4000;
        internal static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left, Top, Right, Bottom;
            public RECT(Rectangle r) { Left = r.Left; Top = r.Top; Right = r.Right; Bottom = r.Bottom; }
            public Rectangle ToRectangle() { return Rectangle.FromLTRB(Left, Top, Right, Bottom); }
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct SIZE { public int Width, Height; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct ThumbnailProperties
        {
            public uint Flags;
            public RECT Destination;
            public RECT Source;
            public byte Opacity;
            [MarshalAs(UnmanagedType.Bool)] public bool Visible;
            [MarshalAs(UnmanagedType.Bool)] public bool SourceClientAreaOnly;
        }

        internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr parameter);
        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd, out RECT rectangle);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maximum);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowTextLength(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] internal static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("shcore.dll")] private static extern int SetProcessDpiAwareness(int value);
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
        [DllImport("dwmapi.dll")] internal static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnail);
        [DllImport("dwmapi.dll")] internal static extern int DwmUnregisterThumbnail(IntPtr thumbnail);
        [DllImport("dwmapi.dll")] internal static extern int DwmQueryThumbnailSourceSize(IntPtr thumbnail, out SIZE size);
        [DllImport("dwmapi.dll")] internal static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref ThumbnailProperties properties);
        [DllImport("dwmapi.dll")] internal static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
        [DllImport("dwmapi.dll")] internal static extern int DwmFlush();

        internal static void EnableDpiAwareness()
        {
            try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } catch (EntryPointNotFoundException) { }
            try { SetProcessDpiAwareness(2); } catch (DllNotFoundException) { SetProcessDPIAware(); }
        }
        internal static Point CursorPosition()
        {
            POINT p; return GetCursorPos(out p) ? new Point(p.X, p.Y) : new Point(Int32.MinValue, Int32.MinValue);
        }
        internal static string Title(IntPtr hwnd)
        {
            int length = Math.Min(GetWindowTextLength(hwnd), 4096);
            StringBuilder builder = new StringBuilder(length + 1);
            GetWindowText(hwnd, builder, builder.Capacity);
            return builder.ToString();
        }
        internal static List<WindowItem> ListWindows()
        {
            List<WindowItem> result = new List<WindowItem>();
            uint ownPid = (uint)Process.GetCurrentProcess().Id;
            EnumWindows(delegate(IntPtr hwnd, IntPtr unused)
            {
                uint pid; GetWindowThreadProcessId(hwnd, out pid);
                if (pid == ownPid || !IsWindowVisible(hwnd)) return true;
                string title = Title(hwnd); RECT r;
                if (String.IsNullOrWhiteSpace(title) || !GetWindowRect(hwnd, out r) || r.Right - r.Left < 80 || r.Bottom - r.Top < 60) return true;
                string name;
                try { using (Process process = Process.GetProcessById((int)pid)) name = process.ProcessName; }
                catch { name = "应用"; }
                result.Add(new WindowItem(hwnd, pid, title, name));
                return true;
            }, IntPtr.Zero);
            result.Sort(delegate(WindowItem a, WindowItem b)
            {
                int priority = BrowserPriority(a.ProcessName).CompareTo(BrowserPriority(b.ProcessName));
                return priority != 0 ? priority : String.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase);
            });
            return result;
        }
        private static int BrowserPriority(string process)
        { return process == "msedge" || process == "chrome" || process == "firefox" ? 0 : 1; }
    }

    internal sealed class WindowItem
    {
        internal readonly IntPtr Handle;
        internal readonly uint ProcessId;
        internal readonly string Title, ProcessName;
        internal WindowItem(IntPtr handle, uint pid, string title, string process)
        { Handle = handle; ProcessId = pid; Title = title; ProcessName = process; }
        public override string ToString() { return "[" + ProcessName + "]  " + Title; }
    }
}
