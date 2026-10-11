using System;
using System.Runtime.InteropServices;

namespace BgmHotkey
{
    internal static class DisplayDpi
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")]
        private static extern IntPtr GetThreadDpiAwarenessContext();
        [DllImport("user32.dll")]
        private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);

        public static void Initialize()
        {
            // Establish the mode before WinForms or any window creates an HWND.
            // The config also enables Framework's per-monitor control resizing.
            try
            {
                IntPtr perMonitorV2 = new IntPtr(-4);
                if (!AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), perMonitorV2))
                    SetProcessDpiAwarenessContext(perMonitorV2);
            }
            catch (EntryPointNotFoundException) { }
        }

        public static string Describe()
        {
            try
            {
                return AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), new IntPtr(-4))
                    ? "PerMonitorV2" : "系统或兼容性 DPI 模式";
            }
            catch (EntryPointNotFoundException) { return "旧版 Windows DPI 模式"; }
        }
    }
}
