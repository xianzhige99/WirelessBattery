using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Win32;

namespace WirelessBattery
{
    public class AudioLinkInfo
    {
        public bool Linked;
        public string Detail = "";
        public int ActiveEndpoints;
        public int TotalEndpoints;
    }

    public static class AudioLink
    {
        private const string RenderRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
        private const string DescKey = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";
        private const string FriendlyKey = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";
        private const string FormFactorKey = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},0";

        // EndpointFormFactor: 3 = Headphones, 5 = Headset (HFP)
        public static AudioLinkInfo IsK02BSLinked()
        {
            AudioLinkInfo result = new AudioLinkInfo();
            try
            {
                using (RegistryKey root = Registry.LocalMachine.OpenSubKey(RenderRoot))
                {
                    if (root == null)
                    {
                        result.Detail = "找不到音频端点注册表";
                        return result;
                    }
                    string[] subs = root.GetSubKeyNames();
                    StringBuilder preferred = new StringBuilder();
                    StringBuilder other = new StringBuilder();
                    for (int i = 0; i < subs.Length; i++)
                    {
                        string sub = subs[i];
                        string desc = "";
                        string friendly = "";
                        int form = -1;
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

                            // 判定依据是“端点描述里的型号名 + 端点是否 Active”，两者都是型号级特征，
                            // 换电脑、换 USB 口、换个体都不会变。formfactor 只用来排序偏好：
                            // 3 = Headphones、5 = Headset 优先，2.4G 接收器只暴露扬声器端点
                            // （formfactor=1）时同样计入，因为它的 DeviceState 也随真实链路切换。
                            result.TotalEndpoints++;
                            int state = 0;
                            using (RegistryKey dev = root.OpenSubKey(sub))
                            {
                                if (dev != null)
                                {
                                    object st = dev.GetValue("DeviceState");
                                    if (st is int) state = (int)st;
                                    else if (st is uint) state = (int)(uint)st;
                                }
                            }
                            bool active = (state & 0x0F) == 1;
                            if (active) result.ActiveEndpoints++;
                            string label = string.Format("{0}({1}) state={2}",
                                string.IsNullOrEmpty(friendly) ? "?" : friendly, form, state);
                            if (form == 3 || form == 5)
                            {
                                if (preferred.Length > 0) preferred.Append(" / ");
                                preferred.Append(label);
                            }
                            else
                            {
                                if (other.Length > 0) other.Append(" / ");
                                other.Append(label);
                            }
                        }
                        catch { }
                    }
                    string summary = preferred.ToString();
                    if (summary.Length > 0 && other.Length > 0) summary += " / ";
                    summary += other.ToString();
                    result.Linked = result.ActiveEndpoints > 0;
                    result.Detail = result.TotalEndpoints == 0
                        ? "没有找到 K02BS 的任何音频端点（接收器可能未插）"
                        : (result.Linked ? "2.4G 音频链路已激活: " : "2.4G 音频链路未激活: ") + summary;
                }
            }
            catch (Exception ex)
            {
                result.Detail = "读取音频端点失败: " + ex.Message;
            }
            return result;
        }
    }
}
