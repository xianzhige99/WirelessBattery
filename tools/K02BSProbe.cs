using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace WirelessBattery
{
    /// <summary>
    /// K02BS 连接状态诊断探针（只读，不改任何设置）。
    ///
    /// 每 2 秒采样一帧，记录三类互相独立的证据：
    ///   1) 音频端点：注册表里所有描述含 K02BS 的 Render/Capture 端点，它们的 formfactor 与 DeviceState；
    ///   2) 蓝牙节点：SetupAPI 枚举到的 BTHENUM 设备节点，含 CM 状态位（是否在场 / 问题码）；
    ///   3) 蓝牙 API：bthprops 的 fConnected 标志（区分「已配对但断开」与「真正连着」）。
    ///
    /// 用法：K02BSProbe.exe [秒数]  默认 300 秒。日志同时写控制台与 k02bs-probe.log。
    /// </summary>
    internal static class K02BSProbe
    {
        private const string RenderRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
        private const string CaptureRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture";
        private const string DescKey = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";
        private const string FriendlyKey = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";
        private const string FormFactorKey = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},0";

        private const int DIGCF_PRESENT = 0x02;
        private const int DIGCF_ALLCLASSES = 0x04;
        private const int SPDRP_FRIENDLYNAME = 0x0C;
        private const int SPDRP_DEVICEDESC = 0x00;
        private const uint DN_PRESENT = 0x00000001;
        private const uint DN_DRIVER_LOADED = 0x00000002;

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public int DevInst;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string enumerator, IntPtr hwnd, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref SP_DEVINFO_DATA data, StringBuilder id, int size, IntPtr required);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA data, int prop, IntPtr type, byte[] buf, int size, IntPtr required);

        [DllImport("setupapi.dll")]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_DevNode_Status(out uint status, out uint problem, int devInst, int flags);

        // ---- 蓝牙 API（bthprops.cpl）----
        [StructLayout(LayoutKind.Sequential)]
        private struct BLUETOOTH_ADDRESS
        {
            public ulong ullLong;
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
        private struct SYSTEMTIME
        {
            public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
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

        /// <summary>BluetoothFindFirstRadio 的参数结构只有 dwSize 一个字段，不能传 search params。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct BLUETOOTH_FIND_RADIO_PARAMS
        {
            public int dwSize;
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

        private static int Main(string[] args)
        {
            int seconds = 300;
            if (args != null && args.Length > 0) int.TryParse(args[0], out seconds);
            if (seconds <= 0) seconds = 300;

            Console.WriteLine("K02BS 连接状态探针：每 2 秒一帧，共 " + seconds + " 秒。");
            Console.WriteLine("请在采样期间把耳机关机再开机，日志会自动记录差异。");
            Console.WriteLine();

            StringBuilder all = new StringBuilder();
            DateTime start = DateTime.Now;
            string lastSignature = "";
            while ((DateTime.Now - start).TotalSeconds < seconds)
            {
                int t = (int)(DateTime.Now - start).TotalSeconds;
                StringBuilder frame = new StringBuilder();
                frame.AppendLine("== t=" + t + "s  " + DateTime.Now.ToString("HH:mm:ss") + " ==");
                try { DumpAudio(frame); } catch (Exception ex) { frame.AppendLine("  audio error " + ex.Message); }
                try { DumpBtNodes(frame); } catch (Exception ex) { frame.AppendLine("  btnode error " + ex.Message); }
                try { DumpBtApi(frame); } catch (Exception ex) { frame.AppendLine("  btapi error " + ex.Message); }

                string signature = ExtractSignature(frame);
                if (signature != lastSignature)
                {
                    lastSignature = signature;
                    Console.Write(frame.ToString());
                }
                all.AppendLine(frame.ToString());
                Thread.Sleep(2000);
            }

            string path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "k02bs-probe.log");
            System.IO.File.WriteAllText(path, all.ToString(), Encoding.UTF8);
            Console.WriteLine();
            Console.WriteLine("采样结束，完整日志：" + path);
            return 0;
        }

        /// <summary>只把真正变化的那几行当作“状态签名”，避免 2 秒一条刷屏。</summary>
        private static string ExtractSignature(StringBuilder frame)
        {
            StringBuilder sig = new StringBuilder();
            string[] lines = frame.ToString().Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string s = lines[i].Trim();
                if (s.Length == 0 || s.StartsWith("==")) continue;
                sig.Append(s).Append('|');
            }
            return sig.ToString();
        }

        private static void DumpAudio(StringBuilder sb)
        {
            string render = DescribeEndpoints(RenderRoot);
            string capture = DescribeEndpoints(CaptureRoot);
            sb.AppendLine("  AUDIO  render: " + render);
            sb.AppendLine("  AUDIO  capture: " + capture);
        }

        private static string DescribeEndpoints(string rootPath)
        {
            try
            {
                using (RegistryKey root = Registry.LocalMachine.OpenSubKey(rootPath))
                {
                    if (root == null) return "(no key)";
                    string[] subs = root.GetSubKeyNames();
                    StringBuilder sb = new StringBuilder();
                    int found = 0;
                    for (int i = 0; i < subs.Length; i++)
                    {
                        string desc = "";
                        int form = -1;
                        int state = 0;
                        try
                        {
                            using (RegistryKey props = root.OpenSubKey(subs[i] + "\\Properties"))
                            {
                                if (props == null) continue;
                                desc = props.GetValue(DescKey) as string;
                                object ff = props.GetValue(FormFactorKey);
                                if (ff is int) form = (int)ff;
                                else if (ff is uint) form = (int)(uint)ff;
                            }
                            if (string.IsNullOrEmpty(desc)) continue;
                            if (desc.IndexOf("K02BS", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            using (RegistryKey dev = root.OpenSubKey(subs[i]))
                            {
                                if (dev != null)
                                {
                                    object st = dev.GetValue("DeviceState");
                                    if (st is int) state = (int)st;
                                    else if (st is uint) state = (int)(uint)st;
                                }
                            }
                        }
                        catch { continue; }
                        if (found > 0) sb.Append("; ");
                        sb.Append(desc + "(ff=" + form + ",state=0x" + state.ToString("X") + ")");
                        found++;
                    }
                    return found == 0 ? "(none)" : sb.ToString();
                }
            }
            catch (Exception ex) { return "(error " + ex.Message + ")"; }
        }

        private static void DumpBtNodes(StringBuilder sb)
        {
            string present = EnumBtNodes(true);
            string allNodes = EnumBtNodes(false);
            sb.AppendLine("  BTNODE present: " + present);
            sb.AppendLine("  BTNODE all    : " + allNodes);
        }

        private static string EnumBtNodes(bool onlyPresent)
        {
            IntPtr set = IntPtr.Zero;
            try
            {
                int flags = DIGCF_ALLCLASSES | (onlyPresent ? DIGCF_PRESENT : 0);
                set = SetupDiGetClassDevs(IntPtr.Zero, "BTHENUM", IntPtr.Zero, flags);
                if (set == IntPtr.Zero || set == new IntPtr(-1)) return "(no set)";

                StringBuilder sb = new StringBuilder();
                int found = 0;
                SP_DEVINFO_DATA data = new SP_DEVINFO_DATA();
                data.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
                {
                    string id = GetInstanceId(set, ref data);
                    string name = GetStringProp(set, ref data, SPDRP_FRIENDLYNAME);
                    if (string.IsNullOrEmpty(name)) name = GetStringProp(set, ref data, SPDRP_DEVICEDESC);
                    string text = (id + " " + name).ToUpperInvariant();
                    if (text.IndexOf("K02BS", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    uint status, problem;
                    int cr = CM_Get_DevNode_Status(out status, out problem, data.DevInst, 0);
                    string flag = "?";
                    if (cr == 0)
                    {
                        flag = ((status & DN_PRESENT) != 0 ? "PRESENT" : "absent")
                             + ((status & DN_DRIVER_LOADED) != 0 ? "+drv" : "")
                             + "/prob=" + problem;
                    }
                    if (found > 0) sb.Append("; ");
                    sb.Append(name + " [" + flag + "]");
                    found++;
                }
                return found == 0 ? "(none)" : sb.ToString();
            }
            catch (Exception ex) { return "(error " + ex.Message + ")"; }
            finally
            {
                if (set != IntPtr.Zero && set != new IntPtr(-1)) { try { SetupDiDestroyDeviceInfoList(set); } catch { } }
            }
        }

        private static string GetInstanceId(IntPtr set, ref SP_DEVINFO_DATA data)
        {
            StringBuilder sb = new StringBuilder(512);
            if (SetupDiGetDeviceInstanceId(set, ref data, sb, sb.Capacity, IntPtr.Zero)) return sb.ToString();
            return "";
        }

        private static string GetStringProp(IntPtr set, ref SP_DEVINFO_DATA data, int prop)
        {
            byte[] buf = new byte[1024];
            if (SetupDiGetDeviceRegistryProperty(set, ref data, prop, IntPtr.Zero, buf, buf.Length, IntPtr.Zero))
                return Encoding.Unicode.GetString(buf).TrimEnd('\0');
            return "";
        }

        private static void DumpBtApi(StringBuilder sb)
        {
            IntPtr hFind = IntPtr.Zero;
            try
            {
                BLUETOOTH_DEVICE_SEARCH_PARAMS p = new BLUETOOTH_DEVICE_SEARCH_PARAMS();
                p.dwSize = Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_SEARCH_PARAMS));
                p.fReturnAuthenticated = 1;
                p.fReturnRemembered = 1;
                p.fReturnUnknown = 0;
                p.fReturnConnected = 1;
                p.fIssueInquiry = 0;
                p.cTimeoutMultiplier = 2;

                BLUETOOTH_FIND_RADIO_PARAMS rp = new BLUETOOTH_FIND_RADIO_PARAMS();
                rp.dwSize = Marshal.SizeOf(typeof(BLUETOOTH_FIND_RADIO_PARAMS));
                IntPtr hRadio;
                hFind = BluetoothFindFirstRadio(ref rp, out hRadio);
                if (hFind == IntPtr.Zero)
                {
                    sb.AppendLine("  BTAPI  (no radio, err=" + Marshal.GetLastWin32Error() + ")");
                    return;
                }
                try
                {
                    BLUETOOTH_DEVICE_INFO info = new BLUETOOTH_DEVICE_INFO();
                    info.dwSize = Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_INFO));
                    IntPtr hDev = BluetoothFindFirstDevice(ref p, ref info);
                    if (hDev == IntPtr.Zero)
                    {
                        sb.AppendLine("  BTAPI  (no device, err=" + Marshal.GetLastWin32Error() + ")");
                        return;
                    }
                    try
                    {
                        int found = 0;
                        StringBuilder line = new StringBuilder();
                        do
                        {
                            string name = info.szName == null ? "" : info.szName;
                            bool keep = name.IndexOf("K02BS", StringComparison.OrdinalIgnoreCase) >= 0;
                            if (!keep) keep = true;   // 也把其它设备打出来，便于判断结构是否被正确解析
                            if (found > 0) line.Append("; ");
                            line.Append(name + " conn=" + (info.fConnected != 0 ? "1" : "0")
                                + " rem=" + (info.fRemembered != 0 ? "1" : "0")
                                + " auth=" + (info.fAuthenticated != 0 ? "1" : "0"));
                            found++;
                            if (found > 8) break;
                        } while (BluetoothFindNextDevice(hDev, ref info));
                        sb.AppendLine("  BTAPI  " + line.ToString());
                    }
                    finally { try { BluetoothFindDeviceClose(hDev); } catch { } }
                }
                finally { try { BluetoothFindRadioClose(hFind); } catch { } }
            }
            catch (Exception ex)
            {
                sb.AppendLine("  BTAPI  error " + ex.Message);
            }
        }
    }
}
