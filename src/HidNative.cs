using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace WirelessBattery
{
    public class HidInterfaceInfo
    {
        public string Path;
        public ushort Vid;
        public ushort Pid;
        public ushort Version;
        public string Product = "";
        public string Manufacturer = "";
        public ushort UsagePage;
        public ushort Usage;
        public int InputLen;
        public int OutputLen;
        public int FeatureLen;

        public string VidPid
        {
            get { return string.Format("{0:X4}:{1:X4}", Vid, Pid); }
        }

        public string ReportText
        {
            get { return string.Format("IN {0} / OUT {1} / FEA {2}", InputLen, OutputLen, FeatureLen); }
        }

        public string ShortPath
        {
            get
            {
                if (string.IsNullOrEmpty(Path)) return "";
                int i = Path.LastIndexOf('\\');
                return i >= 0 ? Path.Substring(i + 1) : Path;
            }
        }

        public override string ToString()
        {
            return string.Format("{0} ({1}) UP=0x{2:X4}", Product, VidPid, UsagePage);
        }
    }

    public class ValueCap
    {
        public int ReportType;          // 0=input, 1=output, 2=feature
        public ushort UsagePage;
        public byte ReportId;
        public ushort Usage;
        public bool IsRange;
        public ushort UsageMin;
        public ushort UsageMax;
        public ushort LinkCollection;
        public ushort BitSize;
        public ushort ReportCount;
        public int LogicalMin;
        public int LogicalMax;

        public bool IsBatteryUsage
        {
            get
            {
                if (UsagePage == 0x0006 && (Usage == 0x0020 || (IsRange && UsageMin <= 0x0020 && UsageMax >= 0x0020))) return true;
                if (UsagePage == 0x000D && (Usage == 0x003B || (IsRange && UsageMin <= 0x003B && UsageMax >= 0x003B))) return true;
                if (UsagePage == 0x0085) return true;
                return false;
            }
        }

        public string Name
        {
            get
            {
                if (UsagePage == 0x0006 && Usage == 0x0020) return "Battery Strength";
                if (UsagePage == 0x000D && Usage == 0x003B) return "Battery Strength";
                if (UsagePage == 0x0085) return "Battery System";
                return string.Format("UP 0x{0:X4} / Usage 0x{1:X4}", UsagePage, Usage);
            }
        }
    }

    public static class HidNative
    {
        public const int DIGCF_PRESENT = 0x02;
        public const int DIGCF_DEVICEINTERFACE = 0x10;

        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 1;
        public const uint FILE_SHARE_WRITE = 2;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_OVERLAPPED = 0x40000000;

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_VALUE_CAPS
        {
            public ushort UsagePage;
            public byte ReportID;
            public byte IsAlias;
            public ushort BitField;
            public ushort LinkCollection;
            public ushort LinkUsage;
            public ushort LinkUsagePage;
            public byte IsRange;
            public byte IsStringRange;
            public byte IsDesignatorRange;
            public byte IsAbsolute;
            public byte HasNull;
            public byte Reserved;
            public ushort BitSize;
            public ushort ReportCount;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
            public ushort[] Reserved2;
            public uint UnitsExp;
            public uint Units;
            public int LogicalMin;
            public int LogicalMax;
            public int PhysicalMin;
            public int PhysicalMax;
            public ushort UsageMin;
            public ushort UsageMax;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct OVERLAPPED
        {
            public IntPtr Internal;
            public IntPtr InternalHigh;
            public int Offset;
            public int OffsetHigh;
            public IntPtr hEvent;
        }

        [DllImport("hid.dll")]
        public static extern void HidD_GetHidGuid(out Guid g);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr hwnd, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid g, int index, ref SP_DEVICE_INTERFACE_DATA data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int detailSize, ref int required, IntPtr devInfo);

        [DllImport("setupapi.dll")]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(IntPtr h, byte[] buf, int n, out int read, ref OVERLAPPED ov);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteFile(IntPtr h, byte[] buf, int n, out int written, ref OVERLAPPED ov);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr h, uint ms);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CancelIoEx(IntPtr h, ref OVERLAPPED ov);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateEvent(IntPtr attr, bool manual, bool init, IntPtr name);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetOverlappedResult(IntPtr h, ref OVERLAPPED ov, out int n, bool wait);

        [DllImport("hid.dll")]
        public static extern bool HidD_GetAttributes(IntPtr h, ref HIDD_ATTRIBUTES a);

        [DllImport("hid.dll")]
        public static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr data);

        [DllImport("hid.dll")]
        public static extern bool HidD_FreePreparsedData(IntPtr data);

        [DllImport("hid.dll")]
        public static extern int HidP_GetCaps(IntPtr data, ref HIDP_CAPS caps);

        [DllImport("hid.dll", CharSet = CharSet.Unicode)]
        public static extern bool HidD_GetProductString(IntPtr h, byte[] buf, int len);

        [DllImport("hid.dll", CharSet = CharSet.Unicode)]
        public static extern bool HidD_GetManufacturerString(IntPtr h, byte[] buf, int len);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetFeature(IntPtr h, byte[] buf, int len);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_SetFeature(IntPtr h, byte[] buf, int len);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetInputReport(IntPtr h, byte[] buf, int len);

        [DllImport("hid.dll")]
        public static extern int HidP_GetValueCaps(int type, [Out] HIDP_VALUE_CAPS[] caps, ref ushort len, IntPtr pp);

        [DllImport("hid.dll")]
        public static extern int HidP_GetUsageValue(int type, ushort usagePage, ushort linkCollection, ushort usage, out uint value, IntPtr pp, byte[] report, int reportLength);

        public static List<HidInterfaceInfo> Enumerate()
        {
            List<HidInterfaceInfo> list = new List<HidInterfaceInfo>();
            Guid g;
            HidD_GetHidGuid(out g);
            IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == (IntPtr)(-1) || set == IntPtr.Zero) return list;
            try
            {
                for (int i = 0; ; i++)
                {
                    SP_DEVICE_INTERFACE_DATA d = new SP_DEVICE_INTERFACE_DATA();
                    d.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d)) break;
                    int need = 0;
                    SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, ref need, IntPtr.Zero);
                    if (need <= 0) continue;
                    IntPtr buf = Marshal.AllocHGlobal(need);
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 5);
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref d, buf, need, ref need, IntPtr.Zero)) continue;
                        string path = Marshal.PtrToStringUni((IntPtr)(buf.ToInt64() + 4));
                        if (string.IsNullOrEmpty(path)) continue;
                        HidInterfaceInfo info = ReadInfo(path);
                        if (info != null) list.Add(info);
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            list.Sort(delegate (HidInterfaceInfo a, HidInterfaceInfo b)
            {
                int c = a.Vid.CompareTo(b.Vid);
                if (c != 0) return c;
                c = a.Pid.CompareTo(b.Pid);
                if (c != 0) return c;
                return a.UsagePage.CompareTo(b.UsagePage);
            });
            return list;
        }

        private static HidInterfaceInfo ReadInfo(string path)
        {
            IntPtr h = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == (IntPtr)(-1)) return null;
            try
            {
                HIDD_ATTRIBUTES a = new HIDD_ATTRIBUTES();
                a.Size = Marshal.SizeOf(typeof(HIDD_ATTRIBUTES));
                if (!HidD_GetAttributes(h, ref a)) return null;
                HidInterfaceInfo info = new HidInterfaceInfo();
                info.Path = path;
                info.Vid = a.VendorID;
                info.Pid = a.ProductID;
                info.Version = a.VersionNumber;
                HIDP_CAPS c = new HIDP_CAPS();
                IntPtr pp;
                if (HidD_GetPreparsedData(h, out pp))
                {
                    try
                    {
                        if (HidP_GetCaps(pp, ref c) >= 0)
                        {
                            info.UsagePage = c.UsagePage;
                            info.Usage = c.Usage;
                            info.InputLen = c.InputReportByteLength;
                            info.OutputLen = c.OutputReportByteLength;
                            info.FeatureLen = c.FeatureReportByteLength;
                        }
                    }
                    finally { HidD_FreePreparsedData(pp); }
                }
                byte[] sb = new byte[512];
                if (HidD_GetProductString(h, sb, sb.Length)) info.Product = Wide(sb);
                sb = new byte[512];
                if (HidD_GetManufacturerString(h, sb, sb.Length)) info.Manufacturer = Wide(sb);
                return info;
            }
            finally { CloseHandle(h); }
        }

        private static string Wide(byte[] b)
        {
            string s = Encoding.Unicode.GetString(b);
            int i = s.IndexOf('\0');
            if (i >= 0) s = s.Substring(0, i);
            return s.Trim();
        }

        public static IntPtr Open(string path, bool readWrite, bool overlapped)
        {
            uint access = readWrite ? (GENERIC_READ | GENERIC_WRITE) : 0;
            uint flags = overlapped ? FILE_FLAG_OVERLAPPED : 0;
            return CreateFile(path, access, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, flags, IntPtr.Zero);
        }

        /// <summary>
        /// 只申请读权限并以 overlapped 方式打开。
        /// ReadFile 要求句柄带 GENERIC_READ：用 access=0 的句柄读设备会直接返回
        /// ERROR_ACCESS_DENIED(5)，所有靠“读输入报文”拿应答的协议都会读不到东西。
        /// </summary>
        private static IntPtr OpenRead(string path)
        {
            return CreateFile(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        }

        public static string Hex(byte[] data)
        {
            if (data == null) return "";
            StringBuilder sb = new StringBuilder(data.Length * 3);
            for (int i = 0; i < data.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(data[i].ToString("X2"));
            }
            return sb.ToString();
        }

        public static string Hex(byte[] data, int count)
        {
            if (data == null) return "";
            if (count > data.Length) count = data.Length;
            byte[] tmp = new byte[count];
            Array.Copy(data, tmp, count);
            return Hex(tmp);
        }

        public static List<ValueCap> GetValueCaps(HidInterfaceInfo info)
        {
            List<ValueCap> result = new List<ValueCap>();
            IntPtr h = Open(info.Path, false, false);
            if (h == (IntPtr)(-1)) return result;
            try
            {
                IntPtr pp;
                if (!HidD_GetPreparsedData(h, out pp)) return result;
                try
                {
                    for (int type = 0; type < 3; type++)
                    {
                        HIDP_VALUE_CAPS[] arr = new HIDP_VALUE_CAPS[128];
                        ushort len = 128;
                        int st = HidP_GetValueCaps(type, arr, ref len, pp);
                        if (st < 0) continue;
                        for (int i = 0; i < len; i++)
                        {
                            ValueCap v = new ValueCap();
                            v.ReportType = type;
                            v.UsagePage = arr[i].UsagePage;
                            v.ReportId = arr[i].ReportID;
                            v.IsRange = arr[i].IsRange != 0;
                            v.UsageMin = arr[i].UsageMin;
                            v.UsageMax = arr[i].UsageMax;
                            v.Usage = arr[i].IsRange != 0 ? arr[i].UsageMin : arr[i].UsageMin;
                            v.LinkCollection = arr[i].LinkCollection;
                            v.BitSize = arr[i].BitSize;
                            v.ReportCount = arr[i].ReportCount;
                            v.LogicalMin = arr[i].LogicalMin;
                            v.LogicalMax = arr[i].LogicalMax;
                            result.Add(v);
                        }
                    }
                }
                finally { HidD_FreePreparsedData(pp); }
            }
            finally { CloseHandle(h); }
            return result;
        }

        public static int? GetUsageValue(HidInterfaceInfo info, ValueCap cap, byte[] report)
        {
            IntPtr h = Open(info.Path, false, false);
            if (h == (IntPtr)(-1)) return null;
            try
            {
                IntPtr pp;
                if (!HidD_GetPreparsedData(h, out pp)) return null;
                try
                {
                    uint value;
                    int st = HidP_GetUsageValue(cap.ReportType, cap.UsagePage, cap.LinkCollection, cap.Usage, out value, pp, report, report.Length);
                    if (st < 0) return null;
                    return (int)value;
                }
                finally { HidD_FreePreparsedData(pp); }
            }
            finally { CloseHandle(h); }
        }

        public static byte[] GetFeature(HidInterfaceInfo info, int reportId, int length)
        {
            if (length < 1) return null;
            IntPtr h = Open(info.Path, false, false);
            if (h == (IntPtr)(-1)) return null;
            try
            {
                byte[] buf = new byte[length];
                buf[0] = (byte)reportId;
                if (!HidD_GetFeature(h, buf, buf.Length)) return null;
                return buf;
            }
            finally { CloseHandle(h); }
        }

        public static bool SetFeature(HidInterfaceInfo info, byte[] data)
        {
            IntPtr h = Open(info.Path, true, false);
            if (h == (IntPtr)(-1)) return false;
            try
            {
                return HidD_SetFeature(h, data, data.Length);
            }
            finally { CloseHandle(h); }
        }

        public static byte[] GetInputReport(HidInterfaceInfo info, int reportId, int length)
        {
            if (length < 1) return null;
            IntPtr h = Open(info.Path, false, false);
            if (h == (IntPtr)(-1)) return null;
            try
            {
                byte[] buf = new byte[length];
                buf[0] = (byte)reportId;
                if (!HidD_GetInputReport(h, buf, buf.Length)) return null;
                return buf;
            }
            finally { CloseHandle(h); }
        }

        public static List<byte[]> ReadInputReports(HidInterfaceInfo info, int milliseconds, int maxReports)
        {
            List<byte[]> result = new List<byte[]>();
            IntPtr h = OpenRead(info.Path);
            if (h == (IntPtr)(-1)) return result;
            IntPtr ev = CreateEvent(IntPtr.Zero, true, false, IntPtr.Zero);
            try
            {
                int len = info.InputLen > 0 ? info.InputLen : 64;
                byte[] buf = new byte[len];
                DateTime end = DateTime.Now.AddMilliseconds(milliseconds);
                while (DateTime.Now < end && result.Count < maxReports)
                {
                    OVERLAPPED ov = new OVERLAPPED();
                    ov.hEvent = ev;
                    int read;
                    bool ok = ReadFile(h, buf, len, out read, ref ov);
                    int err = Marshal.GetLastWin32Error();
                    if (!ok && err == 997)
                    {
                        uint w = WaitForSingleObject(ev, 180);
                        if (w == 0)
                        {
                            GetOverlappedResult(h, ref ov, out read, false);
                            ok = true;
                        }
                        else
                        {
                            CancelIoEx(h, ref ov);
                            WaitForSingleObject(ev, 50);
                            continue;
                        }
                    }
                    if (ok && read > 0)
                    {
                        byte[] copy = new byte[read];
                        Array.Copy(buf, copy, read);
                        result.Add(copy);
                    }
                    else if (!ok)
                    {
                        break;
                    }
                }
            }
            finally
            {
                CloseHandle(h);
                CloseHandle(ev);
            }
            return result;
        }

        public static bool WriteOutput(HidInterfaceInfo info, byte[] data, int timeoutMs)
        {
            IntPtr h = Open(info.Path, true, true);
            if (h == (IntPtr)(-1)) return false;
            IntPtr ev = CreateEvent(IntPtr.Zero, true, false, IntPtr.Zero);
            try
            {
                OVERLAPPED ov = new OVERLAPPED();
                ov.hEvent = ev;
                int written;
                bool ok = WriteFile(h, data, data.Length, out written, ref ov);
                int err = Marshal.GetLastWin32Error();
                if (!ok && err == 997)
                {
                    uint w = WaitForSingleObject(ev, (uint)timeoutMs);
                    if (w == 0)
                    {
                        GetOverlappedResult(h, ref ov, out written, false);
                        ok = true;
                    }
                    else
                    {
                        CancelIoEx(h, ref ov);
                    }
                }
                return ok;
            }
            finally
            {
                CloseHandle(h);
                CloseHandle(ev);
            }
        }
    }
}
