using System;
using System.Collections.Generic;

namespace WirelessBattery
{
    /// <summary>
    /// 协议种类。路由层只按“型号级”标识（VID/PID + 接口类型）决定用哪一种，
    /// 不使用实例路径、端口号、Container ID、序列号、音频端点 ID。
    /// </summary>
    public enum ProtocolKind
    {
        Unknown,
        MchoseFeature,
        MchoseV2,
        Jieli,
        HidActions,
        LogitechPercentage,
        LogitechVoltage,
        GenericHid
    }

    /// <summary>
    /// 命中的电量类型。电压特征只记录原始毫伏，换算留给后续任务。
    /// </summary>
    public enum ProbeValueKind
    {
        None,
        Percentage,
        Voltage
    }

    /// <summary>
    /// 一个型号的协议探测记录。字段全部是型号级信息：
    /// 协议种类、命中类型、罗技特性号、时间戳、连续失败次数、bcdDevice 指纹。
    /// </summary>
    public class ProbeRecord
    {
        public ProtocolKind Protocol = ProtocolKind.Unknown;
        public ProbeValueKind ValueKind = ProbeValueKind.None;
        public int FeatureId;
        public DateTime StampUtc = DateTime.MinValue;
        public DateTime UntilUtc = DateTime.MinValue;
        public int Fails;
        public int Firmware = -1;
        /// <summary>命中时罗技设备所在的 HID++ 设备槽位（0xFF 是接收器自身，1..6 为配对设备）。</summary>
        public int DeviceIndex = -1;

        public bool HasProtocol
        {
            get { return Protocol != ProtocolKind.Unknown; }
        }

        /// <summary>缓存是否仍可信：未过期，且固件指纹没有变化。</summary>
        public bool IsValid(int firmware)
        {
            if (UntilUtc <= DateTime.UtcNow) return false;
            if (firmware >= 0 && Firmware >= 0 && firmware != Firmware) return false;
            return true;
        }
    }

    /// <summary>
    /// 型号级协议路由 + 探测缓存。
    /// 首次遇到某型号跑一遍完整探测链，命中结果写入配置；之后按缓存直读，不再逐个试协议。
    /// 未识别（设备明确回答了“没有这个特性”）也写缓存并带有效期，避免每轮刷新都重探。
    /// 超时/无响应不写负缓存，保留原有“10 秒内自动重试”的行为。
    /// </summary>
    public class ProtocolRouter
    {
        /// <summary>同一型号两次探测的最小间隔。</summary>
        public const int ProbeIntervalSeconds = 30;
        /// <summary>“未识别”缓存的有效分钟数。</summary>
        public const int UnknownValidMinutes = 30;
        /// <summary>“未识别”缓存的上限分钟数（探测次数到顶后）。</summary>
        public const int UnknownValidCapMinutes = 360;
        /// <summary>连续读取失败多少次后允许重新探测。</summary>
        public const int RetryAfterFails = 2;
        /// <summary>单次会话内同一型号的探测次数上限。</summary>
        public const int MaxProbesPerSession = 3;
        /// <summary>成功命中的缓存有效期（天）。固件指纹变化会提前失效。</summary>
        public const int SuccessValidDays = 3650;

        private readonly AppSettings _settings;
        private readonly object _sync = new object();
        // 下面三个表只存内存：它们是“这次连接里协议怎么寻址”，不是身份，不落盘。
        private readonly Dictionary<string, int> _deviceIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _lastProbeUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _probeCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>“明确回答没有电池特性”的连续次数（只存内存，不落盘）。</summary>
        private readonly Dictionary<string, int> _noBatteryStrikes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public ProtocolRouter(AppSettings settings)
        {
            _settings = settings ?? new AppSettings();
        }

        /// <summary>型号级键：大写十六进制 VID_PID，例如 3837_100A、046D_C539。</summary>
        public static string ModelKey(int vid, int pid)
        {
            return string.Format("{0:X4}_{1:X4}", vid, pid);
        }

        public static string ModelKeyOf(HidGroup g)
        {
            if (g == null) return "";
            return ModelKey(g.Vid, g.Pid);
        }

        /// <summary>
        /// 固件指纹：取各组接口 bcdDevice 里最大的那个。不能取“第一份接口”，
        /// 接口枚举顺序在不同次启动之间并不稳定，取首项会让缓存每次启动都判失效、
        /// 于是每次启动都重新跑一遍 10 秒探测。
        /// </summary>
        public static int FirmwareHint(HidGroup g)
        {
            if (g == null || g.Interfaces == null || g.Interfaces.Count == 0) return -1;
            int hint = -1;
            for (int i = 0; i < g.Interfaces.Count; i++)
            {
                int v = g.Interfaces[i].Version;
                if (v > hint) hint = v;
            }
            return hint;
        }

        public ProbeRecord Find(string modelKey)
        {
            if (string.IsNullOrEmpty(modelKey)) return null;
            lock (_sync)
            {
                ProbeRecord rec;
                if (_settings.Probes.TryGetValue(modelKey, out rec)) return rec;
                return null;
            }
        }

        /// <summary>该型号是否处于“明确未识别”的有效期内（期内不探测也不发包）。</summary>
        public bool IsBlocked(string modelKey, int firmware)
        {
            ProbeRecord rec = Find(modelKey);
            if (rec == null) return false;
            if (rec.HasProtocol) return false;
            return rec.IsValid(firmware);
        }

        /// <summary>缓存记录是否可直接用来读值。</summary>
        public bool IsUsable(ProbeRecord rec, int firmware)
        {
            if (rec == null || !rec.HasProtocol || !rec.IsValid(firmware)) return false;
            if (rec.Fails >= RetryAfterFails) return false;
            // 罗技路由只能来自真实探测：FeatureId<=0 的记录是“补写的百分比路由”，
            // 拿它去读会把只有电压特征（0x1001）的机型当成有 0x1004，每轮都失败。
            if (IsLogitechRoute(rec.Protocol) && rec.FeatureId <= 0) return false;
            return true;
        }

        public static bool IsLogitechRoute(ProtocolKind protocol)
        {
            return protocol == ProtocolKind.LogitechPercentage || protocol == ProtocolKind.LogitechVoltage;
        }

        /// <summary>是否允许现在跑一次探测：同型号 30 秒内不重复探测。</summary>
        public bool CanProbe(string modelKey)
        {
            if (string.IsNullOrEmpty(modelKey)) return false;
            lock (_sync)
            {
                DateTime last;
                if (!_lastProbeUtc.TryGetValue(modelKey, out last)) return true;
                return (DateTime.UtcNow - last).TotalSeconds >= ProbeIntervalSeconds;
            }
        }

        public void NoteProbeAttempt(string modelKey)
        {
            if (string.IsNullOrEmpty(modelKey)) return;
            lock (_sync)
            {
                _lastProbeUtc[modelKey] = DateTime.UtcNow;
                int n;
                _probeCount.TryGetValue(modelKey, out n);
                _probeCount[modelKey] = n + 1;
            }
        }

        public int ProbeCount(string modelKey)
        {
            if (string.IsNullOrEmpty(modelKey)) return 0;
            lock (_sync)
            {
                int n;
                _probeCount.TryGetValue(modelKey, out n);
                return n;
            }
        }

        /// <summary>本次读取成功：清零失败计数，缓存继续有效。</summary>
        public void NoteHealthy(string modelKey)
        {
            ProbeRecord rec = Find(modelKey);
            if (rec == null) return;
            lock (_sync)
            {
                rec.Fails = 0;
                _probeCount[modelKey] = 0;
                _noBatteryStrikes.Remove(modelKey);
            }
        }

        /// <summary>超时/无响应：只累计失败次数，不动有效期，保留原有的快速重试。</summary>
        public void NoteTimeout(string modelKey)
        {
            if (string.IsNullOrEmpty(modelKey)) return;
            ProbeRecord rec = Find(modelKey);
            if (rec == null) return;
            lock (_sync) { rec.Fails = rec.Fails + 1; }
        }

        /// <summary>探测命中：写入型号级缓存。</summary>
        public void RecordSuccess(string modelKey, ProtocolKind protocol, ProbeValueKind kind, int featureId, int firmware, int deviceIndex)
        {
            if (string.IsNullOrEmpty(modelKey) || protocol == ProtocolKind.Unknown) return;
            DateTime now = DateTime.UtcNow;
            lock (_sync)
            {
                ProbeRecord rec;
                if (!_settings.Probes.TryGetValue(modelKey, out rec))
                {
                    rec = new ProbeRecord();
                    _settings.Probes[modelKey] = rec;
                }
                rec.Protocol = protocol;
                rec.ValueKind = kind;
                rec.FeatureId = featureId;
                rec.Firmware = firmware;
                rec.StampUtc = now;
                rec.UntilUtc = now.AddDays(SuccessValidDays);
                rec.Fails = 0;
                if (deviceIndex >= 0) rec.DeviceIndex = deviceIndex;
                _probeCount[modelKey] = 0;
                _noBatteryStrikes.Remove(modelKey);
            }
        }

        /// <summary>
        /// 首次成功读取时补一条路由记录（只对单变体协议用，不改变任何读取行为）。
        /// </summary>
        public void EnsureRecord(string modelKey, ProtocolKind protocol, ProbeValueKind kind, int firmware)
        {
            if (string.IsNullOrEmpty(modelKey) || protocol == ProtocolKind.Unknown) return;
            // 罗技的百分比/电压是两条不同路径，不能凭“这张卡有电量”补写路由：
            // 只有探测才能确定命中哪一条、命中哪个特性号。
            if (IsLogitechRoute(protocol)) return;
            lock (_sync)
            {
                if (_settings.Probes.ContainsKey(modelKey)) return;
            }
            RecordSuccess(modelKey, protocol, kind, 0, firmware, -1);
        }

        /// <summary>
        /// “明确回答没有电池特性”的连续次数：只有连续两轮都这样回答才写负缓存，
        /// 避免模式切换/刚开机那几秒的偶发应答把型号打进 30 分钟黑名单。
        /// </summary>
        public int BumpNoBatteryStrike(string modelKey)
        {
            if (string.IsNullOrEmpty(modelKey)) return 0;
            lock (_sync)
            {
                int n;
                _noBatteryStrikes.TryGetValue(modelKey, out n);
                n = n + 1;
                _noBatteryStrikes[modelKey] = n;
                return n;
            }
        }

        public void ResetNoBatteryStrike(string modelKey)
        {
            if (string.IsNullOrEmpty(modelKey)) return;
            lock (_sync) { _noBatteryStrikes.Remove(modelKey); }
        }

        /// <summary>
        /// 探测链跑完没有任何协议可用（设备明确回答“没有这个特性”）：写负缓存。
        /// 探测次数到顶后有效期拉长，避免无限重试。
        /// </summary>
        public void RecordUnsupported(string modelKey, int firmware)
        {
            if (string.IsNullOrEmpty(modelKey)) return;
            DateTime now = DateTime.UtcNow;
            lock (_sync)
            {
                ProbeRecord rec;
                if (!_settings.Probes.TryGetValue(modelKey, out rec))
                {
                    rec = new ProbeRecord();
                    _settings.Probes[modelKey] = rec;
                }
                rec.Protocol = ProtocolKind.Unknown;
                rec.ValueKind = ProbeValueKind.None;
                rec.FeatureId = 0;
                rec.Firmware = firmware;
                rec.StampUtc = now;
                rec.Fails = rec.Fails + 1;
                int n;
                _probeCount.TryGetValue(modelKey, out n);
                int minutes = n >= MaxProbesPerSession ? UnknownValidCapMinutes : UnknownValidMinutes;
                rec.UntilUtc = now.AddMinutes(minutes);
            }
        }

        /// <summary>清除某型号的缓存（设备重新插拔、后台上报设备变更时调用）。</summary>
        public void ClearModel(string modelKey)
        {
            if (string.IsNullOrEmpty(modelKey)) return;
            lock (_sync)
            {
                _settings.Probes.Remove(modelKey);
                _deviceIndex.Remove(modelKey);
                _lastProbeUtc.Remove(modelKey);
                _probeCount.Remove(modelKey);
            }
        }

        /// <summary>清除全部缓存。线程安全：可能在界面线程被调用。</summary>
        public void InvalidateAll()
        {
            lock (_sync)
            {
                _settings.Probes.Clear();
                _deviceIndex.Clear();
                _lastProbeUtc.Clear();
                _probeCount.Clear();
            }
        }

        public void BeginScan()
        {
            lock (_sync) { _present.Clear(); }
        }

        public void NotePresent(string modelKey)
        {
            if (string.IsNullOrEmpty(modelKey)) return;
            lock (_sync) { _present.Add(modelKey); }
        }

        /// <summary>
        /// 扫描结束：把这次枚举里已经不存在的型号缓存清掉，
        /// 这样设备重新插拔后会重新探测，而不是一直用旧结论。
        /// </summary>
        public void EndScan()
        {
            List<string> stale = new List<string>();
            lock (_sync)
            {
                foreach (string key in _settings.Probes.Keys)
                {
                    if (!_present.Contains(key)) stale.Add(key);
                }
                for (int i = 0; i < stale.Count; i++)
                {
                    _settings.Probes.Remove(stale[i]);
                    _deviceIndex.Remove(stale[i]);
                    _lastProbeUtc.Remove(stale[i]);
                    _probeCount.Remove(stale[i]);
                }
            }
        }

        /// <summary>
        /// 记住罗技设备所在的槽位（1..6）。命中缓存直读成功时也要补记一次：
        /// 否则每次重启都得先去找槽位，找不到就退到 0xFF，读到“有人应答但没有电量”。
        /// </summary>
        public void RememberDeviceIndex(string modelKey, int deviceIndex)
        {
            if (string.IsNullOrEmpty(modelKey) || deviceIndex <= 0) return;
            ProbeRecord rec = Find(modelKey);
            if (rec != null)
            {
                lock (_sync) { rec.DeviceIndex = deviceIndex; }
            }
            SetCachedDeviceIndex(modelKey, deviceIndex);
        }

        public int GetCachedDeviceIndex(string modelKey)
        {
            lock (_sync)
            {
                int v;
                if (_deviceIndex.TryGetValue(modelKey, out v)) return v;
                return -1;
            }
        }

        public void SetCachedDeviceIndex(string modelKey, int deviceIndex)
        {
            lock (_sync) { _deviceIndex[modelKey] = deviceIndex; }
        }

        /// <summary>协议名，用于写入 settings.ini 的 Type/Proto 字段。</summary>
        public static string ProtocolName(ProtocolKind kind)
        {
            switch (kind)
            {
                case ProtocolKind.MchoseFeature: return "MchoseFeature";
                case ProtocolKind.MchoseV2: return "MchoseV2";
                case ProtocolKind.Jieli: return "Jieli";
                case ProtocolKind.HidActions: return "HidActions";
                case ProtocolKind.LogitechPercentage: return "LogitechPercentage";
                case ProtocolKind.LogitechVoltage: return "LogitechVoltage";
                case ProtocolKind.GenericHid: return "GenericHid";
                default: return "Unknown";
            }
        }

        public static ProtocolKind ParseProtocol(string text)
        {
            if (string.IsNullOrEmpty(text)) return ProtocolKind.Unknown;
            switch (text.Trim().ToLowerInvariant())
            {
                case "mchosefeature": return ProtocolKind.MchoseFeature;
                case "mchosev2": return ProtocolKind.MchoseV2;
                case "jieli": return ProtocolKind.Jieli;
                case "hidactions": return ProtocolKind.HidActions;
                case "logitechpercentage": return ProtocolKind.LogitechPercentage;
                case "logitechvoltage": return ProtocolKind.LogitechVoltage;
                case "generichid": return ProtocolKind.GenericHid;
                default: return ProtocolKind.Unknown;
            }
        }

        /// <summary>命中类型的可读名（百分比 / 电压），写入 settings.ini 的 Type 字段。</summary>
        public static string ValueKindName(ProbeValueKind kind)
        {
            switch (kind)
            {
                case ProbeValueKind.Percentage: return "Percentage";
                case ProbeValueKind.Voltage: return "Voltage";
                default: return "None";
            }
        }

        public static ProbeValueKind ParseValueKind(string text)
        {
            if (string.IsNullOrEmpty(text)) return ProbeValueKind.None;
            switch (text.Trim().ToLowerInvariant())
            {
                case "percentage": return ProbeValueKind.Percentage;
                case "voltage": return ProbeValueKind.Voltage;
                default: return ProbeValueKind.None;
            }
        }
    }
}
