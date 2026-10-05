using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BgmHotkey
{
    internal sealed class HotkeyService : IDisposable
    {
        private const int HookKeyboardLowLevel = 13;
        private const int WmKeyDown = 0x0100;
        private const int WmKeyUp = 0x0101;
        private const int WmSysKeyDown = 0x0104;
        private const int WmSysKeyUp = 0x0105;
        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardData
        {
            public uint VirtualKey;
            public uint ScanCode;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        private delegate IntPtr HookProcedure(int code, IntPtr message, IntPtr data);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int hookId, HookProcedure procedure, IntPtr module, uint threadId);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string moduleName);

        private readonly MainMenuForm _menu;
        private readonly HashSet<int> _pressed = new HashSet<int>();
        private readonly HookProcedure _callback;
        private IntPtr _hook;

        public HotkeyService(MainMenuForm menu)
        {
            _menu = menu;
            _callback = HookCallback;
        }

        public string Install()
        {
            using (Process process = Process.GetCurrentProcess())
            using (ProcessModule module = process.MainModule)
            {
                _hook = SetWindowsHookEx(HookKeyboardLowLevel, _callback, GetModuleHandle(module.ModuleName), 0);
            }
            if (_hook == IntPtr.Zero)
                return new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return "";
        }

        private IntPtr HookCallback(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0)
            {
                int messageId = message.ToInt32();
                KeyboardData keyboard = (KeyboardData)Marshal.PtrToStructure(data, typeof(KeyboardData));
                int key = unchecked((int)keyboard.VirtualKey);
                if (messageId == WmKeyDown || messageId == WmSysKeyDown)
                {
                    bool isNewPress = _pressed.Add(key);
                    if (isNewPress && !_menu.IsCapturingKey)
                    {
                        _menu.PlayHotkey(((Keys)key).ToString());
                    }
                }
                else if (messageId == WmKeyUp || messageId == WmSysKeyUp)
                {
                    _pressed.Remove(key);
                }
            }
            return CallNextHookEx(_hook, code, message, data);
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }
    }
}
