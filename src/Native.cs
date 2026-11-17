using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HelmetDesk
{
    /// <summary>Windows API calls and screen checks used by the windows (rounded corners, restoring a minimized window, saved positions).</summary>
    internal static class Native
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr hwnd, int cmd);

        public const int SW_RESTORE = 9;

        /// <summary>True when a window whose top-left corner is at (x, y) would still be reachable (its title bar is on some monitor).
        /// A monitor that was unplugged leaves saved positions pointing at nothing.</summary>
        public static bool OnScreen(double x, double y)
        {
            var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            return vs.Contains(new Point(x + 40, y + 10));
        }

        /// <summary>Asks Windows 11 to draw rounded corners on a borderless window (ignored on Windows 10).</summary>
        public static void Round(Window w)
        {
            try
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                int pref = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(hwnd, 33, ref pref, sizeof(int));
            }
            catch { }
        }
    }
}
