namespace WirelessBattery
{
    /// <summary>K02BS 的链路判定结果。只描述“我们观察到了什么”，不涉及界面文案。</summary>
    public enum K02BSLinkState
    {
        /// <summary>接收器不在，也没有蓝牙链路。</summary>
        None,
        /// <summary>只有接收器在线（存在 K02BS 音频端点），但耳机是否开机/连上 2.4G 无法确认。</summary>
        DongleOnly,
        /// <summary>蓝牙链路通，接收器不在。</summary>
        Bluetooth,
        /// <summary>接收器与蓝牙都在。</summary>
        Dual
    }

    /// <summary>
    /// 纯判定逻辑：不读注册表、不碰界面，便于用真值表直接断言。
    ///
    /// 依据（2026-10-30 实测）：
    ///   - 蓝牙链路：ff=3/5 端点激活，或 Windows 蓝牙 HFP 已上报电量，二者任一成立即可；
    ///   - 2.4G 链路：**没有**可观测信号（ff=1 恒激活、HID 0xFF90 恒定），
    ///     因此「只有接收器在线」不能等同于「耳机已连接」。
    /// </summary>
    public static class K02BSLinkJudge
    {
        public static K02BSLinkState Decide(bool donglePresent, bool bluetoothEndpointActive, bool bluetoothBatteryPresent)
        {
            bool bt = bluetoothEndpointActive || bluetoothBatteryPresent;
            if (bt && donglePresent) return K02BSLinkState.Dual;
            if (bt) return K02BSLinkState.Bluetooth;
            if (donglePresent) return K02BSLinkState.DongleOnly;
            return K02BSLinkState.None;
        }

        /// <summary>
        /// 该状态是否算作「已连接」。
        /// DongleOnly 默认不算（耳机可能已经关机）；给「只用 2.4G」的用户留了一个开关。
        /// </summary>
        public static bool CountsAsConnected(K02BSLinkState state, bool soleDongleAsConnected)
        {
            if (state == K02BSLinkState.None) return false;
            if (state == K02BSLinkState.DongleOnly) return soleDongleAsConnected;
            return true;
        }

        public static string ModeText(K02BSLinkState state)
        {
            switch (state)
            {
                case K02BSLinkState.Dual: return "双模";
                case K02BSLinkState.Bluetooth: return "蓝牙";
                case K02BSLinkState.DongleOnly: return "2.4G";
                default: return "未连接";
            }
        }
    }
}
