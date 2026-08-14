using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SalsaNOW
{
    // GFN uses a custom shell (CustomExplorer) that we need to close on startup.
    // Instead of hardcoding the name, we search by window class.
    // That way if NVIDIA renames it, we still find it.
    internal static class GfnShellDetector
    {
        private static readonly string[] KNOWN_NAMES = new string[]
        {
            "CustomExplorer",
        };

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        public static Process FindShellProcess()
        {
            foreach (var name in KNOWN_NAMES)
            {
                var procs = Process.GetProcessesByName(name);
                if (procs.Length > 0)
                {
                    for (int i = 1; i < procs.Length; i++) procs[i].Dispose();
                    return procs[0];
                }
            }
            return null;
        }

        public static IntPtr FindShellWindow()
        {
            foreach (var name in KNOWN_NAMES)
            {
                IntPtr hWnd = NativeMethods.FindWindowByCaption(IntPtr.Zero, name);
                if (hWnd != IntPtr.Zero) return hWnd;
            }
            return IntPtr.Zero;
        }
    }
}
