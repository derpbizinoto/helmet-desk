using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HelmetDesk
{
    /// <summary>Windows API calls used by the windows (rounded corners, restoring a minimized window).</summary>
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
