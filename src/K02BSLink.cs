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
    /// 输入只有两路证据：
    ///   - donglePresent：存在任一 K02BS 音频端点 → 接收器在线；
    ///   - bluetoothLinked：`ff=3/5` 端点激活，或蓝牙 API 报 fConnected=1 → 蓝牙链路通。
    ///
    /// **「Windows 里有 HFP 电量属性」不算连接证据**：设备断开后系统不会立刻清掉该属性，
    /// 关机状态下仍能读到 80% 的旧值（2026-10-30 22:57 实测）。
    /// 「2.4G 链路」本身也没有任何可观测信号（ff=1 恒激活、HID 0xFF90 恒定），
    /// 因此「只有接收器在线」不等于「耳机已连接」。
    /// </summary>
    public static class K02BSLinkJudge
    {
        public static K02BSLinkState Decide(bool donglePresent, bool bluetoothLinked)
        {
            if (bluetoothLinked && donglePresent) return K02BSLinkState.Dual;
            if (bluetoothLinked) return K02BSLinkState.Bluetooth;
            if (donglePresent) return K02BSLinkState.DongleOnly;
            return K02BSLinkState.None;
        }

        /// <summary>
        /// 该状态是否算作「已连接」。只有接收器在线（DongleOnly）**不算**：
        /// 2.4G 链路不回报耳机状态，接收器插着不等于耳机在用。
        /// </summary>
        public static bool CountsAsConnected(K02BSLinkState state)
        {
            return state == K02BSLinkState.Dual || state == K02BSLinkState.Bluetooth;
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
