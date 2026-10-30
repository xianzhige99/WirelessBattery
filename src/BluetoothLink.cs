using System;
using System.Runtime.InteropServices;

namespace WirelessBattery
{
    /// <summary>
    /// 用 bthprops 的 fConnected 判断某个蓝牙设备当前是否真的连着。
    ///
    /// 为什么需要它：Windows 在设备断开后**不会立刻清掉** HFP 电量属性，
    /// 于是「有电量属性」会在关机后残留一段时间，不能当作连接证据。
    /// 而 fConnected 在关机后立刻变 0（实测 22:57 耳机已关机 → conn=0，同时 HFP 属性仍在）。
    ///
    /// 只做只读查询：不发起配对、不做 inquiry（fIssueInquiry=0），单次调用毫秒级。
    /// </summary>
    internal static class BluetoothLink
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct BLUETOOTH_ADDRESS
        {
            public ulong ullLong;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEMTIME
        {
            public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct BLUETOOTH_DEVICE_INFO
        {
            public int dwSize;
            public BLUETOOTH_ADDRESS Address;
            public int ulClassofDevice;
            public int fConnected;
            public int fRemembered;
            public int fAuthenticated;
            public SYSTEMTIME stLastSeen;
            public SYSTEMTIME stLastUsed;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
            public string szName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BLUETOOTH_FIND_RADIO_PARAMS
        {
            public int dwSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BLUETOOTH_DEVICE_SEARCH_PARAMS
        {
            public int dwSize;
            public int fReturnAuthenticated;
            public int fReturnRemembered;
            public int fReturnUnknown;
            public int fReturnConnected;
            public int fIssueInquiry;
            public byte cTimeoutMultiplier;
            public IntPtr hRadio;
        }

        [DllImport("bthprops.cpl", SetLastError = true)]
        private static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS p, out IntPtr hRadio);

        [DllImport("bthprops.cpl", SetLastError = true)]
        private static extern bool BluetoothFindRadioClose(IntPtr hFind);

        [DllImport("bthprops.cpl", SetLastError = true)]
        private static extern IntPtr BluetoothFindFirstDevice(ref BLUETOOTH_DEVICE_SEARCH_PARAMS p, ref BLUETOOTH_DEVICE_INFO info);

        [DllImport("bthprops.cpl", SetLastError = true)]
        private static extern bool BluetoothFindNextDevice(IntPtr hFind, ref BLUETOOTH_DEVICE_INFO info);

        [DllImport("bthprops.cpl", SetLastError = true)]
        private static extern bool BluetoothFindDeviceClose(IntPtr hFind);

        public const int Unknown = -1;

        /// <summary>
        /// 名称包含 namePart 的设备是否处于已连接状态。
        /// 返回 Unknown(-1) 表示查询不可用（没有蓝牙无线电 / API 调用失败），
        /// 调用方应退回到「只看音频端点」的判断，不要当成断开。
        /// </summary>
        public static int QueryConnected(string namePart)
        {
            if (string.IsNullOrEmpty(namePart)) return Unknown;
            IntPtr hFindRadio = IntPtr.Zero;
            IntPtr hRadio = IntPtr.Zero;
            try
            {
                BLUETOOTH_FIND_RADIO_PARAMS rp = new BLUETOOTH_FIND_RADIO_PARAMS();
                rp.dwSize = Marshal.SizeOf(typeof(BLUETOOTH_FIND_RADIO_PARAMS));
                hFindRadio = BluetoothFindFirstRadio(ref rp, out hRadio);
                if (hFindRadio == IntPtr.Zero || hRadio == IntPtr.Zero) return Unknown;

                BLUETOOTH_DEVICE_SEARCH_PARAMS p = new BLUETOOTH_DEVICE_SEARCH_PARAMS();
                p.dwSize = Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_SEARCH_PARAMS));
                p.fReturnAuthenticated = 1;
                p.fReturnRemembered = 1;
                p.fReturnConnected = 1;
                p.fReturnUnknown = 0;
                p.fIssueInquiry = 0;
                p.cTimeoutMultiplier = 1;
                p.hRadio = hRadio;

                BLUETOOTH_DEVICE_INFO info = new BLUETOOTH_DEVICE_INFO();
                info.dwSize = Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_INFO));
                IntPtr hDev = BluetoothFindFirstDevice(ref p, ref info);
                if (hDev == IntPtr.Zero) return Unknown;
                try
                {
                    int seen = 0;
                    do
                    {
                        string name = info.szName == null ? "" : info.szName;
                        if (name.IndexOf(namePart, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return info.fConnected != 0 ? 1 : 0;
                        }
                        seen++;
                    }
                    while (BluetoothFindNextDevice(hDev, ref info) && seen < 32);
                    // 无线电存在但没找到该设备：说明已配对信息被清掉，按未连接处理。
                    return 0;
                }
                finally { try { BluetoothFindDeviceClose(hDev); } catch { } }
            }
            catch { return Unknown; }
            finally
            {
                if (hFindRadio != IntPtr.Zero) { try { BluetoothFindRadioClose(hFindRadio); } catch { } }
                if (hRadio != IntPtr.Zero) { try { CloseHandle(hRadio); } catch { } }
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);
    }
}
