using System;
using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WirelessBattery
{
    internal static class TrayProbe
    {
        private const int NIM_ADD = 0;
        private const int NIM_DELETE = 2;
        private const uint NIF_MESSAGE = 0x01;
        private const uint NIF_ICON = 0x02;
        private const uint NIF_TIP = 0x04;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

        public static bool Test(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                NOTIFYICONDATA data = new NOTIFYICONDATA();
                data.cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA));
                data.hWnd = hwnd;
                data.uID = 0x5A5A;
                data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
                data.uCallbackMessage = 0x0400 + 1;
                data.hIcon = SystemIcons.Application.Handle;
                data.szTip = "设备电量";
                bool ok = Shell_NotifyIcon(NIM_ADD, ref data);
                if (ok)
                {
                    data.uFlags = 0;
                    Shell_NotifyIcon(NIM_DELETE, ref data);
                }
                return ok;
            }
            catch { return false; }
        }

        public static void CleanupStale()
        {
            try
            {
                using (RegistryKey root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", true))
                {
                    if (root == null) return;
                    string[] names = root.GetSubKeyNames();
                    for (int i = 0; i < names.Length; i++)
                    {
                        using (RegistryKey sub = root.OpenSubKey(names[i], false))
                        {
                            if (sub == null) continue;
                            string tip = sub.GetValue("InitialTooltip") as string;
                            object uidValue = sub.GetValue("UID");
                            uint uid = 0;
                            try { if (uidValue != null) uid = Convert.ToUInt32(uidValue); } catch { }
                            if (tip == "设备电量托盘检测" || (uid == 0x5A5A && tip == "设备电量"))
                            {
                                try { root.DeleteSubKeyTree(names[i], false); } catch { }
                            }
                        }
                    }
                }
            }
            catch { }
        }
    }
}
