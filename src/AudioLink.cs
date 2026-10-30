using System;
using System.Text;
using Microsoft.Win32;

namespace WirelessBattery
{
    /// <summary>
    /// K02BS 的音频端点观察结果。
    ///
    /// 实测（2026-10-30，同一接收器、耳机开关机反复切换）：
    ///   - ff=3（耳机）/ ff=5（免提）端点的 DeviceState 在 0x1（激活）与 0x8（未插入）之间翻转，
    ///     跟随真实蓝牙链路；0x80000008 这种高位标志同样按低 4 位判断；
    ///   - ff=1（扬声器）/ ff=4（麦克风）端点**恒为 0x1**——接收器插着就在，耳机开不开机都一样。
    ///
    /// 所以：ff=3/5 激活 = 蓝牙链路通；存在任一 K02BS 端点 = 接收器在线。
    /// 2.4G 的真实链路（耳机是否开机并连上 2.4G）没有可观测信号，不能由 ff=1 推断。
    /// </summary>
    public class AudioLinkInfo
    {
        /// <summary>接收器在线：存在任意一个 K02BS 音频端点。</summary>
        public bool DonglePresent;
        /// <summary>蓝牙链路通：ff=3 / ff=5 端点处于激活态。</summary>
        public bool BluetoothLinked;
        /// <summary>兼容旧调用：等价于 DonglePresent。</summary>
        public bool Linked;
        public string Detail = "";
        public int ActiveEndpoints;
        public int TotalEndpoints;
    }

    public static class AudioLink
    {
        private const string RenderRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
        private const string CaptureRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture";
        private const string DescKey = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";
        private const string FriendlyKey = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";
        private const string FormFactorKey = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},0";

        // EndpointFormFactor: 3 = Headphones，5 = Headset（HFP）。只有这两个跟随蓝牙链路。
        private const int FormFactorHeadphones = 3;
        private const int FormFactorHeadset = 5;

        public static AudioLinkInfo IsK02BSLinked()
        {
            AudioLinkInfo result = new AudioLinkInfo();
            try
            {
                StringBuilder bt = new StringBuilder();
                StringBuilder other = new StringBuilder();
                ScanRoot(RenderRoot, result, bt, other);
                ScanRoot(CaptureRoot, result, bt, other);

                result.DonglePresent = result.TotalEndpoints > 0;
                result.Linked = result.DonglePresent;

                string summary = bt.ToString();
                if (summary.Length > 0 && other.Length > 0) summary += " / ";
                summary += other.ToString();

                if (result.TotalEndpoints == 0)
                    result.Detail = "没有找到 K02BS 的任何音频端点（接收器可能未插）";
                else if (result.BluetoothLinked)
                    result.Detail = "蓝牙链路已激活: " + summary;
                else
                    result.Detail = "接收器在线，蓝牙端点未激活（耳机可能已关机或只走 2.4G）: " + summary;
            }
            catch (Exception ex)
            {
                result.Detail = "读取音频端点失败: " + ex.Message;
            }
            return result;
        }

        private static void ScanRoot(string rootPath, AudioLinkInfo result, StringBuilder bt, StringBuilder other)
        {
            try
            {
                using (RegistryKey root = Registry.LocalMachine.OpenSubKey(rootPath))
                {
                    if (root == null) return;
                    string[] subs = root.GetSubKeyNames();
                    for (int i = 0; i < subs.Length; i++)
                    {
                        string sub = subs[i];
                        string desc = "";
                        string friendly = "";
                        int form = -1;
                        int state = 0;
                        try
                        {
                            using (RegistryKey props = root.OpenSubKey(sub + "\\Properties"))
                            {
                                if (props == null) continue;
                                desc = props.GetValue(DescKey) as string;
                                friendly = props.GetValue(FriendlyKey) as string;
                                object ff = props.GetValue(FormFactorKey);
                                if (ff is int) form = (int)ff;
                                else if (ff is uint) form = (int)(uint)ff;
                            }
                            if (string.IsNullOrEmpty(desc)) continue;
                            if (desc.IndexOf("K02BS", StringComparison.OrdinalIgnoreCase) < 0) continue;

                            using (RegistryKey dev = root.OpenSubKey(sub))
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

                        result.TotalEndpoints++;
                        // 只取低 4 位：0x80000008 这类值的高位是附加标志，不代表激活。
                        bool active = (state & 0x0F) == 1;
                        if (active) result.ActiveEndpoints++;

                        string label = string.Format("{0}(ff={1},state=0x{2:X})",
                            string.IsNullOrEmpty(friendly) ? "?" : friendly, form, state);

                        if (form == FormFactorHeadphones || form == FormFactorHeadset)
                        {
                            if (bt.Length > 0) bt.Append(" / ");
                            bt.Append(label);
                            if (active) result.BluetoothLinked = true;
                        }
                        else
                        {
                            if (other.Length > 0) other.Append(" / ");
                            other.Append(label);
                        }
                    }
                }
            }
            catch { }
        }
    }
}
