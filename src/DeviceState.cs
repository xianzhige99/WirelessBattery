using System;

namespace WirelessBattery
{
    /// <summary>
    /// 一台设备的“状态”：稳定标识、显示名、类别、启用/卡片/托盘/排序、最后在线时间，
    /// 以及阶梯估算配置与运行时计时锚点。
    ///
    /// 稳定标识只允许型号级取值：MCHOSE、K02BS、VID_PID（如 046D:C539）、BT:设备名。
    /// 不存放实例路径、USB 端口号、Container ID、序列号或音频端点 ID —— 换口、换机、换个体都命中同一标识。
    /// </summary>
    public class DeviceState
    {
        public string Id = "";
        public string Title = "";
        public string Category = "";
        public bool Enabled;
        public bool ShowCard;
        public bool ShowTray;
        public int Order;
        public DateTime LastSeenUtc = DateTime.MinValue;
        // 下面两项可能是 null：该型号还没有阶梯配置/运行时状态时，不去凭空创建条目（否则会污染写盘内容）。
        public EstimateConfig Estimate;
        public RuntimeState Runtime;

        /// <summary>按型号级标识推导类别，只看标识本身，不读任何实例信息。</summary>
        public static string CategoryOf(string id)
        {
            if (string.IsNullOrEmpty(id)) return "未知";
            if (string.Equals(id, "MCHOSE", StringComparison.OrdinalIgnoreCase)) return "鼠标";
            if (string.Equals(id, "K02BS", StringComparison.OrdinalIgnoreCase)) return "耳机";
            if (id.StartsWith("BT:", StringComparison.OrdinalIgnoreCase)) return "蓝牙";
            if (id.StartsWith("046D", StringComparison.OrdinalIgnoreCase)) return "罗技外设";
            return "未知";
        }
    }
}
