using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace WirelessBattery
{
    public class BtBatteryInfo
    {
        public string Name = "";
        public string InstanceId = "";
        public int? Level;
        public bool HasProperty;
        public string Detail = "";
    }

    public static class BluetoothBattery
    {
        private const int DIGCF_PRESENT = 0x02;
        private const int DIGCF_ALLCLASSES = 0x04;
        private const int SPDRP_FRIENDLYNAME = 0x0C;
        private const int SPDRP_DEVICEDESC = 0x00;

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public int DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string enumerator, IntPtr hwnd, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref SP_DEVINFO_DATA data, StringBuilder id, int size, IntPtr required);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA data, int prop, IntPtr type, byte[] buf, int size, IntPtr required);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceProperty(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key, out uint type, byte[] value, uint size, out uint required, uint flags);

        [DllImport("setupapi.dll")]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        public static List<BtBatteryInfo> Scan()
        {
            List<BtBatteryInfo> list = new List<BtBatteryInfo>();
            IntPtr set = IntPtr.Zero;
            try
            {
                set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
                if (set == IntPtr.Zero || set == (IntPtr)(-1)) return list;
                DEVPROPKEY key = new DEVPROPKEY();
                key.fmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5");
                key.pid = 2;

                for (int i = 0; ; i++)
                {
                    SP_DEVINFO_DATA d = new SP_DEVINFO_DATA();
                    d.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                    if (!SetupDiEnumDeviceInfo(set, i, ref d)) break;
                    StringBuilder sb = new StringBuilder(512);
                    SetupDiGetDeviceInstanceId(set, ref d, sb, 512, IntPtr.Zero);
                    string iid = sb.ToString();
                    if (string.IsNullOrEmpty(iid)) continue;
                    if (!iid.StartsWith("BTHENUM", StringComparison.OrdinalIgnoreCase)) continue;

                    string name = StrProp(set, ref d, SPDRP_FRIENDLYNAME);
                    if (string.IsNullOrEmpty(name)) name = StrProp(set, ref d, SPDRP_DEVICEDESC);
                    BtBatteryInfo info = new BtBatteryInfo();
                    info.Name = name;
                    info.InstanceId = iid;
                    try
                    {
                        uint type = 0;
                        uint need = 0;
                        byte[] val = new byte[8];
                        bool ok = SetupDiGetDeviceProperty(set, ref d, ref key, out type, val, (uint)val.Length, out need, 0);
                        if (ok && val.Length > 0)
                        {
                            info.HasProperty = true;
                            int level = val[0];
                            if (level >= 0 && level <= 100) info.Level = level;
                            info.Detail = "Windows 蓝牙免提/HFP 电量属性";
                        }
                        else
                        {
                            info.Detail = "没有蓝牙电量属性（未连接或设备不上报）";
                        }
                    }
                    catch (Exception ex)
                    {
                        info.Detail = "读取蓝牙电量失败: " + ex.Message;
                    }
                    list.Add(info);
                }
            }
            catch
            {
            }
            finally
            {
                if (set != IntPtr.Zero && set != (IntPtr)(-1)) SetupDiDestroyDeviceInfoList(set);
            }
            return list;
        }

        private static string StrProp(IntPtr set, ref SP_DEVINFO_DATA d, int prop)
        {
            byte[] b = new byte[1024];
            if (!SetupDiGetDeviceRegistryProperty(set, ref d, prop, IntPtr.Zero, b, b.Length, IntPtr.Zero)) return "";
            string s = Encoding.Unicode.GetString(b);
            int i = s.IndexOf('\0');
            if (i >= 0) s = s.Substring(0, i);
            return s;
        }
    }
}
