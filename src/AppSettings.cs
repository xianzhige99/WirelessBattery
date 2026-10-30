using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WirelessBattery
{
    public class RuntimeState
    {
        public bool Connected;
        public DateTime SinceUtc = DateTime.MinValue;
        public int LastLevel = 100;
        public bool HasLastLevel;
        public bool HasBtStage;
        public int BtStage;
        public DateTime BtStageUtc = DateTime.MinValue;
        public string BtConfigSig = "";
        public DateTime DisconnectedSinceUtc = DateTime.MinValue;
        // 连续“物理不通”的刷新轮数。只存内存，不写 ini：用于 60 秒刷新间隔下的单次失败去抖。
        public int LinkMisses;

        // K02BS mode/3s check/freeze state
        public string LastMode = "";
        public DateTime ModeSinceUtc = DateTime.MinValue;
        public bool ModeCheckDone;
        public bool Frozen;
        public bool HoldFrozen;
        public bool FrozenHas;
        public int FrozenLevel;
        public int FrozenReported;
        public double FrozenHeldHours;
        public bool HasLastDual;
        public int LastDualLevel;
        public int LastDualReported;
        public double LastDualHeldHours;
        // 取消勾选时记下暂停时刻；重新勾选时把这段时间补回 BtStageUtc，阶梯“已保持”从暂停处继续。
        public DateTime PausedUtc = DateTime.MinValue;
    }

    public class EstimateConfig
    {
        // 只有 K02BS 默认开启；其他蓝牙设备按需在卡片上打开。
        public bool Enabled;
        public double FullHours = 100.0;
        public List<int> Stages = new List<int> { 100, 80, 60, 40, 20, 0 };
        // 五个固定权重区间的权重（相对值，总和不必为 100，程序按总和归一化）：
        // 100-80、80-60、60-40、40-20、20-0。区间本身固定，不随 Stages 内容变化。
        public List<double> Weights = DefaultWeights();

        public static List<double> DefaultWeights()
        {
            return new List<double> { 8, 40, 40, 8, 4 };
        }

        public EstimateConfig Clone()
        {
            EstimateConfig c = new EstimateConfig();
            c.Enabled = Enabled;
            c.FullHours = FullHours;
            c.Stages = new List<int>(Stages);
            c.Weights = new List<double>(Weights);
            return c;
        }
    }

    /// <summary>
    /// GPW 电压到百分比的校准参数。默认值即“没有校准过”时的行为：
    /// 满 4200mV、空 3300mV、中段不偏移。缺键一律按默认值，旧配置无损。
    /// </summary>
    public class GpwConfig
    {
        public double FullMv = 4200.0;
        public double EmptyMv = 3300.0;
        public double MidOffset = 0.0;
        public int MedianWindow = 3;
        public double Alpha = 0.25;
        public double Hysteresis = 2.0;
        public double RiseTolerance = 50.0;
        // 全自动调整默认关闭。
        public bool AutoAdjust;
        // 观测最高非充电电压：只读展示，不参与换算。
        public double ObservedMaxMv;
        public int Samples;
        // 充电周期计数与上次自动下调时的周期号：保证“至少隔一个充电周期”。
        public int CycleCount;
        public int LastAutoCycle;
        public DateTime LastAutoAdjustUtc = DateTime.MinValue;
        // 用户手动保存过锚点：该标记一置位，自动调整永久停止。
        public bool ManualEdited;

        public GpwConfig Clone()
        {
            GpwConfig c = new GpwConfig();
            c.FullMv = FullMv;
            c.EmptyMv = EmptyMv;
            c.MidOffset = MidOffset;
            c.MedianWindow = MedianWindow;
            c.Alpha = Alpha;
            c.Hysteresis = Hysteresis;
            c.RiseTolerance = RiseTolerance;
            c.AutoAdjust = AutoAdjust;
            c.ObservedMaxMv = ObservedMaxMv;
            c.Samples = Samples;
            c.CycleCount = CycleCount;
            c.LastAutoCycle = LastAutoCycle;
            c.LastAutoAdjustUtc = LastAutoAdjustUtc;
            c.ManualEdited = ManualEdited;
            return c;
        }
    }

    public class AppSettings
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "WirelessBattery";
        private const string TaskName = "WirelessBattery_AutoStart";
        private readonly string _path;
        // 1 = 有未落盘的改动。运行时状态写得太频繁，改为合并写（见 SaveDeferred/Flush）。
        private int _dirty;

        public int IntervalSeconds = 60;
        public bool CloseToTray = true;
        public bool AutoStart;
        public bool AutoStartWanted = true;
        public bool NeedsAutoStartSetup;
        // K02BS：只有接收器在线（2.4G 端点存在）时是否也算“已连接”。
        // 默认 false —— 2.4G 链路不回报耳机状态，接收器插着不等于耳机在用。
        public bool K02BSSoleDongleAsConnected;
        public Dictionary<string, EstimateConfig> Estimates = new Dictionary<string, EstimateConfig>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, RuntimeState> Runtime = new Dictionary<string, RuntimeState>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, bool> EnabledDevices = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        // 已发现过的型号键（MCHOSE、K02BS、046D:C539、BT:<名称>…）。设置页按它列出可勾选设备，
        // 勾选状态本身仍存在 EnabledDevices 里；持久化为 Device.<键>.Seen=1，旧配置缺该键时按固定两项补齐。
        public List<string> DiscoveredKeys = new List<string>();
        // 型号级协议探测缓存（键形如 3837_100A、046D_C539），只由 ProtocolRouter 读写。
        public Dictionary<string, ProbeRecord> Probes = new Dictionary<string, ProbeRecord>(StringComparer.OrdinalIgnoreCase);
        // 设备显示开关 / 排序位 / 最后在线时间：按型号级键保存。
        // 现阶段只存不消费（是否启用仍由 EnabledDevices 决定），旧版程序会忽略这几行。
        public Dictionary<string, bool> DeviceShowCard = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, bool> DeviceShowTray = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> DeviceOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, DateTime> DeviceLastSeen = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        // 自定义显示名（为空即用识别到的默认名；只影响显示，不参与设备识别）。
        public Dictionary<string, string> DeviceNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // GPW 电压校准参数（全局一份：锚点是电池化学层面的量，不跟具体个体走）。
        public GpwConfig Gpw = new GpwConfig();

        public static readonly string[] KnownDeviceKeys = new string[] { "MCHOSE", "K02BS" };
        public static string DeviceTitle(string key)
        {
            if (string.Equals(key, "MCHOSE", StringComparison.OrdinalIgnoreCase)) return "MCHOSE A7 V2 Pro+";
            if (string.Equals(key, "K02BS", StringComparison.OrdinalIgnoreCase)) return "西伯利亚 K02BS";
            if (key.StartsWith("BT:", StringComparison.OrdinalIgnoreCase)) return "蓝牙 " + key.Substring(3);
            return key;
        }

        /// <summary>设置页用的标题：有自定义名就用自定义名，否则用型号默认名。</summary>
        public string TitleOf(string key)
        {
            if (key != null)
            {
                string custom;
                if (DeviceNames.TryGetValue(key, out custom) && !string.IsNullOrEmpty(custom)) return custom;
            }
            return DeviceTitle(key);
        }

        /// <summary>写自定义名；传空（或只剩空白/控制字符）即删除，回到识别到的默认名。</summary>
        public void SetDeviceName(string key, string name)
        {
            if (key == null) return;
            string clean = ConfigStore.SanitizeName(name);
            if (clean.Length == 0) DeviceNames.Remove(key);
            else DeviceNames[key] = clean;
        }

        public string GetDeviceName(string key)
        {
            if (key == null) return "";
            string custom;
            if (DeviceNames.TryGetValue(key, out custom)) return custom == null ? "" : custom;
            return "";
        }

        public AppSettings()
        {
            // 便携模式：设置和估算计时放在 exe 同目录。
            _path = Path.Combine(Application.StartupPath, "settings.ini");
            EstimateConfig k02 = new EstimateConfig();
            k02.Enabled = true;
            k02.FullHours = 163.0;
            Estimates["K02BS"] = k02;
            EnabledDevices["MCHOSE"] = true;
            EnabledDevices["K02BS"] = true;
            DiscoveredKeys.Add("MCHOSE");
            DiscoveredKeys.Add("K02BS");
        }

        public static AppSettings Load()
        {
            AppSettings s = new AppSettings();
            // 文件格式、读取容错、旧版迁移都收敛在 ConfigStore；这里只做加载后的业务补种与自启探测。
            ConfigStore.Load(s._path, s);

            // 补种可勾选型号：旧配置没有 Seen 键时至少列出原来的两个固定型号；
            // 配置文件里出现过 Device.<键>.Enabled 的型号也算“已发现”，保证勾选项不丢。
            for (int i = 0; i < KnownDeviceKeys.Length; i++) s.SeedDiscovered(KnownDeviceKeys[i]);
            foreach (KeyValuePair<string, bool> kv in s.EnabledDevices) s.SeedDiscovered(kv.Key);

            // 单行越界在上面已被逐条忽略；这里只兜底“两项各自合法但互相矛盾”（空电 ≥ 满电）的情况。
            string gpwReason;
            if (!IsValidGpw(s.Gpw, out gpwReason)) s.Gpw = new GpwConfig();

            s.AutoStart = s.IsAutoStartEnabled();
            if (s.AutoStartWanted && IsFinalExeName())
            {
                // 旧版只写了“启动”文件夹快捷方式；这里在后台升级为计划任务。
                // 如果当前已经是管理员，还把旧的低权限任务升级为 HighestAvailable，
                // 避免登录后任务以普通权限运行、再次遇到 Explorer 高权限导致的托盘失败。
                bool task = TaskExists();
                if (!task) s.NeedsAutoStartSetup = true;
                else if (Elevation.IsElevated() && !TaskIsHighest()) s.NeedsAutoStartSetup = true;
            }
            return s;
        }
        // ---- 设备状态（型号级）：把散落在各字典里的字段聚成一份可读视图 ----

        /// <summary>
        /// 取某型号的状态视图。只做只读聚合，不会凭空创建 Estimate/Runtime 条目
        /// （否则会改变写盘内容）；还没有配置时对应字段为 null。
        /// </summary>
        public DeviceState GetDeviceState(string deviceKey)
        {
            DeviceState st = new DeviceState();
            st.Id = deviceKey ?? "";
            st.Title = TitleOf(st.Id);
            st.Category = DeviceState.CategoryOf(st.Id);
            st.Enabled = IsDeviceEnabled(st.Id);
            bool flag;
            st.ShowCard = DeviceShowCard.TryGetValue(st.Id, out flag) ? flag : st.Enabled;
            st.ShowTray = DeviceShowTray.TryGetValue(st.Id, out flag) ? flag : st.Enabled;
            int order;
            st.Order = DeviceOrder.TryGetValue(st.Id, out order) ? order : OrderOf(st.Id);
            DateTime seen;
            st.LastSeenUtc = DeviceLastSeen.TryGetValue(st.Id, out seen) ? seen : DateTime.MinValue;
            EstimateConfig cfg;
            st.Estimate = Estimates.TryGetValue(st.Id, out cfg) ? cfg : null;
            RuntimeState rt;
            st.Runtime = Runtime.TryGetValue(st.Id, out rt) ? rt : null;
            return st;
        }

        /// <summary>把状态里的显示开关与排序写回（启用状态与计时各有专用入口，不在此处改）。</summary>
        public void ApplyDeviceState(DeviceState st)
        {
            if (st == null || string.IsNullOrEmpty(st.Id)) return;
            DeviceShowCard[st.Id] = st.ShowCard;
            DeviceShowTray[st.Id] = st.ShowTray;
            DeviceOrder[st.Id] = st.Order;
            if (st.LastSeenUtc != DateTime.MinValue) DeviceLastSeen[st.Id] = st.LastSeenUtc;
        }

        /// <summary>枚举到该型号时更新“最后在线时间”（只改内存，随既有 Save 落盘，不额外增加写盘次数）。</summary>
        public void NoteSeen(string deviceKey)
        {
            if (string.IsNullOrEmpty(deviceKey)) return;
            DeviceLastSeen[deviceKey] = DateTime.UtcNow;
        }

        /// <summary>该型号最后一次被枚举到的时间（UTC）；从未见过返回 MinValue。</summary>
        public DateTime GetLastSeenUtc(string deviceKey)
        {
            DateTime seen;
            if (!string.IsNullOrEmpty(deviceKey) && DeviceLastSeen.TryGetValue(deviceKey, out seen)) return seen;
            return DateTime.MinValue;
        }

        private int OrderOf(string deviceKey)
        {
            List<string> keys = GetDeviceKeys();
            for (int i = 0; i < keys.Count; i++)
            {
                if (string.Equals(keys[i], deviceKey, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return keys.Count;
        }
        /// <summary>校验 GPW 参数；不合法只给原因，绝不自动修正。</summary>
        public static bool IsValidGpw(GpwConfig c, out string reason)
        {
            reason = "";
            if (c == null) { reason = "参数为空"; return false; }
            if (c.FullMv < 3300 || c.FullMv > 4500) { reason = "满电锚点必须在 3300~4500 mV"; return false; }
            if (c.EmptyMv < 3000) { reason = "空电锚点不能低于 3000 mV"; return false; }
            if (c.EmptyMv > 4500) { reason = "空电锚点必须在 3000~4500 mV"; return false; }
            if (c.EmptyMv >= c.FullMv) { reason = "空电锚点必须低于满电锚点"; return false; }
            if (c.MidOffset < -10 || c.MidOffset > 10) { reason = "中段偏移必须在 -10% ~ +10%"; return false; }
            if (c.MedianWindow < 1 || c.MedianWindow > 7) { reason = "中值窗口必须在 1~7"; return false; }
            if (c.Alpha < 0.1 || c.Alpha > 0.5) { reason = "EMA α 必须在 0.1~0.5"; return false; }
            if (c.Hysteresis < 0 || c.Hysteresis > 20) { reason = "滞回死区必须在 0~20%"; return false; }
            if (c.RiseTolerance < 0 || c.RiseTolerance > 500) { reason = "回升容差必须在 0~500 mV"; return false; }
            return true;
        }

        public GpwConfig GetGpwConfig()
        {
            if (Gpw == null) Gpw = new GpwConfig();
            return Gpw;
        }

        /// <summary>写回校准参数：先校验，不合法直接拒绝（不保存、不修正）。</summary>
        public bool SetGpwConfig(GpwConfig cfg, out string reason)
        {
            if (!IsValidGpw(cfg, out reason)) return false;
            Gpw = cfg;
            Save();
            return true;
        }

        /// <summary>观测值/周期计数这类运行时字段只在内存改动，由调用方决定何时落盘。</summary>
        public void SaveGpwRuntime()
        {
            try { Save(); }
            catch { }
        }

        public void Save()
        {
            // 原子写、写锁、键名拼装都在 ConfigStore；这里保持“写失败不抛异常”的原有语义。
            Interlocked.Exchange(ref _dirty, 0);
            ConfigStore.Save(_path, this);
        }

        /// <summary>
        /// 只在内存里改、把落盘推迟到下次 Flush()。用于每轮刷新都会变的运行时状态
        /// （最后电量、连接状态、K02BS 双模快照），避免 60 秒一次刷新就重写整个 settings.ini。
        /// </summary>
        public void SaveDeferred()
        {
            Interlocked.Exchange(ref _dirty, 1);
        }

        /// <summary>把挂起的改动落盘；没有改动就什么都不做。由 TrayContext 的合并写定时器与 Exit 调用。</summary>
        public void Flush()
        {
            if (Interlocked.Exchange(ref _dirty, 0) == 0) return;
            try { ConfigStore.Save(_path, this); }
            catch { }
        }

        /// <summary>是否有尚未落盘的改动（仅诊断用）。</summary>
        public bool HasPendingSave
        {
            get { return Interlocked.CompareExchange(ref _dirty, 0, 0) != 0; }
        }
        /// <summary>
        /// 把旧键上的估算/运行状态搬到新的型号级键。
        /// 旧键原样保留在字典里（下次 Save 仍会写回），做到无损迁移。
        /// </summary>
        public void MigrateKey(string fromKey, string toKey)
        {
            if (string.IsNullOrEmpty(fromKey) || string.IsNullOrEmpty(toKey)) return;
            if (string.Equals(fromKey, toKey, StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                EstimateConfig cfg;
                if (!Estimates.ContainsKey(toKey) && Estimates.TryGetValue(fromKey, out cfg))
                    Estimates[toKey] = cfg;
                RuntimeState st;
                if (!Runtime.ContainsKey(toKey) && Runtime.TryGetValue(fromKey, out st))
                    Runtime[toKey] = st;
            }
            catch { }
        }

        public EstimateConfig GetEstimateConfig(string deviceKey)
        {
            EstimateConfig cfg;
            if (!Estimates.TryGetValue(deviceKey, out cfg))
            {
                cfg = new EstimateConfig();
                cfg.Enabled = deviceKey.Equals("K02BS", StringComparison.OrdinalIgnoreCase);
                cfg.FullHours = deviceKey.Equals("K02BS", StringComparison.OrdinalIgnoreCase) ? 163.0 : 100.0;
                Estimates[deviceKey] = cfg;
            }
            if (cfg.Stages == null || cfg.Stages.Count < 2)
                cfg.Stages = new List<int> { 100, 80, 60, 40, 20, 0 };
            return cfg;
        }

        public void SetEstimateConfig(string deviceKey, EstimateConfig cfg)
        {
            Estimates[deviceKey] = cfg;
            Save();
        }

        public static string FormatStages(List<int> stages)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < stages.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(stages[i].ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>
        /// 阶梯配置自身是否合法：2 个以上、0~100、从高到低、不重复。
        /// </summary>
        public static bool IsValidStageSet(EstimateConfig cfg, out string reason)
        {
            reason = "";
            if (cfg == null || cfg.Stages == null || cfg.Stages.Count < 2)
            {
                reason = "至少需要 2 个阶梯";
                return false;
            }
            int prev = 101;
            for (int i = 0; i < cfg.Stages.Count; i++)
            {
                int v = cfg.Stages[i];
                if (v < 0 || v > 100)
                {
                    reason = "阶梯数值必须在 0~100 之间";
                    return false;
                }
                if (v >= prev)
                {
                    reason = "阶梯必须从高到低且不能重复";
                    return false;
                }
                prev = v;
            }
            return true;
        }

        private static string ConfigSignature(EstimateConfig cfg)
        {
            return cfg.FullHours.ToString("0.###", CultureInfo.InvariantCulture) + "|" + FormatStages(cfg.Stages);
        }

        /// <summary>五个固定权重区间，与 EstimateConfig.Weights 一一对应：100-80、80-60、60-40、40-20、20-0。</summary>
        public static readonly int[] WeightHighs = new int[] { 100, 80, 60, 40, 20 };
        public static readonly int[] WeightLows = new int[] { 80, 60, 40, 20, 0 };

        /// <summary>
        /// 解析配置里的权重串：必须正好五项、每项为正数、总和为正数，否则返回 null
        /// （调用方退回默认权重，不自动修正用户填的数）。
        /// </summary>
        public static List<double> ParseWeights(string text)
        {
            if (text == null) return null;
            string[] parts = text.Split(new char[] { ',', '，', ' ', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 5) return null;
            List<double> weights = new List<double>();
            double sum = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                double v;
                if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return null;
                if (!(v > 0)) return null;
                weights.Add(v);
                sum += v;
            }
            if (!(sum > 0)) return null;
            return weights;
        }

        public static string FormatWeights(List<double> weights)
        {
            if (weights == null) return FormatWeights(EstimateConfig.DefaultWeights());
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < weights.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(weights[i].ToString("0.###", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>
        /// 权重必须正好五项、每项为正数、总和为正数。
        /// 不合法时只返回原因，绝不自动修正（设置窗口据此拒绝保存并提示）。
        /// </summary>
        public static bool IsValidWeights(List<double> weights, out string reason)
        {
            reason = "";
            if (weights == null || weights.Count != 5)
            {
                reason = "需要 5 个区间的权重（100-80、80-60、60-40、40-20、20-0）";
                return false;
            }
            double sum = 0;
            for (int i = 0; i < weights.Count; i++)
            {
                if (!(weights[i] > 0))
                {
                    reason = string.Format("第 {0} 段的权重必须是正数（区间 {1}-{2}）", i + 1, WeightLows[i], WeightHighs[i]);
                    return false;
                }
                sum += weights[i];
            }
            if (!(sum > 0))
            {
                reason = "权重总和必须大于 0";
                return false;
            }
            return true;
        }

        /// <summary>某段百分比区间所覆盖的“权重质量”：与区间重叠多宽，就按比例分走该区间的多少权重。</summary>
        private static double WeightMass(List<double> weights, int low, int high)
        {
            double mass = 0;
            for (int i = 0; i < weights.Count && i < 5; i++)
            {
                int lo = WeightLows[i];
                int hi = WeightHighs[i];
                int a = Math.Max(low, lo);
                int b = Math.Min(high, hi);
                if (b <= a) continue;
                mass += (b - a) * (weights[i] / (double)(hi - lo));
            }
            return mass;
        }

        /// <summary>
        /// 某个阶梯段的时长（小时）：阶梯段 [next, reported] 在百分比轴上盖住哪些固定区间，
        /// 就按覆盖宽度比例从这些区间分得权重。
        /// 分母取“整条阶梯覆盖范围的权重质量”，因此所有阶梯段时长之和正好等于满电小时数，
        /// 无论阶梯最低档是不是 0%。（权重为相对值，改后按新总和重新归一化。）
        /// </summary>
        public static double SegmentHours(EstimateConfig cfg, int reported, int next)
        {
            if (cfg == null || cfg.Stages == null || cfg.Stages.Count < 2) return 0;
            if (reported <= next) return 0;
            List<double> weights = cfg.Weights;
            string reason;
            if (!IsValidWeights(weights, out reason)) weights = EstimateConfig.DefaultWeights();
            int low = cfg.Stages[0];
            int high = cfg.Stages[0];
            for (int i = 1; i < cfg.Stages.Count; i++)
            {
                if (cfg.Stages[i] < low) low = cfg.Stages[i];
                if (cfg.Stages[i] > high) high = cfg.Stages[i];
            }
            double chainMass = WeightMass(weights, low, high);
            if (!(chainMass > 0)) return 0;
            double segMass = WeightMass(weights, next, reported);
            if (segMass < 0) segMass = 0;
            if (segMass > chainMass) segMass = chainMass;
            return cfg.FullHours * segMass / chainMass;
        }

        /// <summary>五个区间各占的比例（0~1），供设置界面显示与预览共用同一套算法。</summary>
        public static double[] NormalizedShares(List<double> weights)
        {
            double[] shares = new double[5];
            List<double> w = weights;
            string reason;
            if (!IsValidWeights(w, out reason)) w = EstimateConfig.DefaultWeights();
            double sum = 0;
            for (int i = 0; i < 5; i++) sum += w[i];
            if (!(sum > 0)) return shares;
            for (int i = 0; i < 5; i++) shares[i] = w[i] / sum;
            return shares;
        }

        /// <summary>五个区间各自的预计时长（小时），供设置界面预览。</summary>
        public static double[] IntervalHours(List<double> weights, double fullHours)
        {
            double[] hours = new double[5];
            double[] shares = NormalizedShares(weights);
            for (int i = 0; i < 5; i++) hours[i] = shares[i] * fullHours;
            return hours;
        }

        public RuntimeState GetRuntime(string deviceKey)
        {
            RuntimeState st;
            if (!Runtime.TryGetValue(deviceKey, out st))
            {
                st = new RuntimeState();
                Runtime[deviceKey] = st;
            }
            return st;
        }

        public void MarkConnected(string deviceKey)
        {
            RuntimeState st = GetRuntime(deviceKey);
            if (!st.Connected || st.SinceUtc == DateTime.MinValue)
            {
                st.Connected = true;
                st.SinceUtc = DateTime.UtcNow;
                st.LastLevel = 100;
                st.HasLastLevel = true;
                SaveDeferred();
            }
        }

        public void MarkDisconnected(string deviceKey)
        {
            RuntimeState st = GetRuntime(deviceKey);
            if (st.Connected)
            {
                st.Connected = false;
                SaveDeferred();
            }
        }

        /// <summary>
        /// 带冗余的连接状态：物理上不通时先记一次“漏拍”，连续 2 次且持续 ≥90 秒才真正判“未连接”。
        /// 期间 pending=true，界面显示“连接波动”并沿用 10.5 秒快速重试；
        /// 60 秒刷新间隔下单次偶发失败不再让卡片直接翻成“未连接”。
        /// </summary>
        public bool UpdateLinkState(string deviceKey, bool physicallyConnected, out bool pending)
        {
            RuntimeState st = GetRuntime(deviceKey);
            bool changed = false;
            pending = false;

            if (physicallyConnected)
            {
                if (st.DisconnectedSinceUtc != DateTime.MinValue)
                {
                    st.DisconnectedSinceUtc = DateTime.MinValue;
                    changed = true;
                }
                if (st.LinkMisses != 0) st.LinkMisses = 0;
                if (!st.Connected)
                {
                    st.Connected = true;
                    changed = true;
                }
                if (changed) SaveDeferred();
                return true;
            }

            bool wasConnected = st.Connected || st.HasLastLevel;
            if (!wasConnected)
            {
                pending = false;
                if (st.Connected) { st.Connected = false; SaveDeferred(); }
                return false;
            }

            st.LinkMisses = st.LinkMisses + 1;
            if (st.DisconnectedSinceUtc == DateTime.MinValue)
            {
                st.DisconnectedSinceUtc = DateTime.UtcNow;
                changed = true;
            }

            double seconds = (DateTime.UtcNow - st.DisconnectedSinceUtc).TotalSeconds;
            if (st.LinkMisses >= 2 && seconds >= 90.0)
            {
                pending = false;
                if (st.Connected) { st.Connected = false; changed = true; }
            }
            else
            {
                pending = true;
            }
            if (changed) SaveDeferred();
            return pending || st.Connected;
        }

        public void SetLastLevel(string deviceKey, int level)
        {
            RuntimeState st = GetRuntime(deviceKey);
            if (!st.HasLastLevel || st.LastLevel != level)
            {
                st.HasLastLevel = true;
                st.LastLevel = level;
                SaveDeferred();
            }
        }

        public int? GetLastLevel(string deviceKey)
        {
            RuntimeState st = GetRuntime(deviceKey);
            return st.HasLastLevel ? (int?)st.LastLevel : null;
        }

        public bool IsDeviceEnabled(string deviceKey)
        {
            bool enabled = true;
            if (EnabledDevices.TryGetValue(deviceKey, out enabled)) return enabled;
            return true;
        }

        public void SetDeviceEnabled(string deviceKey, bool enabled)
        {
            // 顺手补一条“已发现”：否则这个键不会出现在 GetDeviceKeys 里，
            // 勾选状态也就不会被写进 settings.ini（表现为“取消勾选重启后又回来了”）。
            SeedDiscovered(deviceKey);
            EnabledDevices[deviceKey] = enabled;
            Save();
        }

        // ---- 型号级勾选（门控用）：可勾选列表 = 已发现型号，勾选状态 = EnabledDevices ----

        internal void SeedDiscovered(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (ContainsIgnore(DiscoveredKeys, key)) return;
            DiscoveredKeys.Add(key);
        }

        /// <summary>扫描时发现新型号：加入可勾选列表并默认勾选。返回是否为新型号（调用方据此决定是否落盘）。</summary>
        public bool NoteDiscovered(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (ContainsIgnore(DiscoveredKeys, key)) return false;
            DiscoveredKeys.Add(key);
            if (!EnabledDevices.ContainsKey(key)) EnabledDevices[key] = true;
            return true;
        }

        /// <summary>设置页要列出的型号：原来的两个固定型号在前，其余按名字排序。</summary>
        public List<string> GetDeviceKeys()
        {
            List<string> list = new List<string>();
            list.Add("MCHOSE");
            list.Add("K02BS");
            List<string> others = new List<string>();
            for (int i = 0; i < DiscoveredKeys.Count; i++)
            {
                string key = DiscoveredKeys[i];
                if (string.IsNullOrEmpty(key)) continue;
                if (string.Equals(key, "MCHOSE", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(key, "K02BS", StringComparison.OrdinalIgnoreCase)) continue;
                if (ContainsIgnore(others, key)) continue;
                others.Add(key);
            }
            others.Sort(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < others.Count; i++) list.Add(others[i]);
            return list;
        }

        private static bool ContainsIgnore(List<string> list, string key)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], key, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>取消勾选：记下暂停时刻，阶梯“已保持”不再随时间增长。</summary>
        public void PauseLadder(string key)
        {
            RuntimeState st = GetRuntime(key);
            if (st.PausedUtc != DateTime.MinValue) return;
            st.PausedUtc = DateTime.UtcNow;
            Save();
        }

        /// <summary>重新勾选：把暂停期间的时间补回各计时基准，阶梯从暂停处继续。</summary>
        public void ResumeLadder(string key)
        {
            RuntimeState st = GetRuntime(key);
            if (st.PausedUtc == DateTime.MinValue) return;
            TimeSpan gap = DateTime.UtcNow - st.PausedUtc;
            if (gap > TimeSpan.Zero)
            {
                if (st.HasBtStage && st.BtStageUtc != DateTime.MinValue) st.BtStageUtc = st.BtStageUtc + gap;
                if (st.ModeSinceUtc != DateTime.MinValue) st.ModeSinceUtc = st.ModeSinceUtc + gap;
                if (st.DisconnectedSinceUtc != DateTime.MinValue) st.DisconnectedSinceUtc = st.DisconnectedSinceUtc + gap;
            }
            st.PausedUtc = DateTime.MinValue;
            Save();
        }

        /// <summary>
        /// 创建“已启用但未发现”的占位卡片；有上次电量就显示上次电量。
        /// </summary>
        public DeviceCardModel CreatePlaceholderCard(string key)
        {
            bool isK02 = string.Equals(key, "K02BS", StringComparison.OrdinalIgnoreCase);
            DeviceCardModel card = new DeviceCardModel();
            card.DeviceKey = key;
            card.Title = TitleOf(key);
            card.Subtitle = isK02
                ? "VID 10D6 · PID 4801 · 2.4G / 蓝牙 游戏耳机"
                : "VID 3837 · PID 100A · 迈从 2.4G 鼠标";
            card.Mode = isK02 ? "双模" : "2.4G";
            card.ThemeColor = isK02 ? Color.FromArgb(46, 144, 250) : Color.FromArgb(18, 183, 106);
            card.Connected = false;
            int? level = GetLastLevel(key);
            card.HasLevel = level.HasValue;
            if (level.HasValue)
            {
                card.Level = level.Value;
                card.LevelText = level.Value + "%";
            }
            else card.LevelText = "—";
            card.Charging = false;
            card.StatusText = "未连接";
            card.StatusLine = "未连接 · 已启用显示";
            card.EstimateHint = level.HasValue ? "最后电量" : "";
            card.Detail = "未发现设备；按设置保留此卡片";
            card.Source = "设备卡片占位";
            card.Accent = level.HasValue
                ? (level.Value < 30 ? Color.FromArgb(240, 68, 56) : card.ThemeColor)
                : Color.FromArgb(152, 162, 179);
            card.CanEstimate = isK02;
            card.EstimateEnabled = isK02 && GetEstimateConfig(key).Enabled;
            return card;
        }

        /// <summary>
        /// 启动时最多恢复“多少小时内见过”的型号卡片。超过这个窗口的型号只保留
        /// MCHOSE / K02BS 两个固定卡片，其余等扫描发现后再出现。
        /// </summary>
        public const int StaleCardHours = 24;

        /// <summary>
        /// 启动时的“上次状态”卡片：用上次关闭前保存的电量/连接/模式先把卡片画出来，
        /// 避免首轮 HID 读取（尤其接收器探测、MCHOSE 休眠重试）期间窗口空着。
        ///
        /// 只恢复“上次关闭时还连着”的设备，外加两个固定型号（MCHOSE / K02BS）。
        /// 很久以前出现过、但现在早就不在场的型号不再画成一张过期卡片——
        /// 它们等首轮扫描真的发现设备时再出现。
        /// </summary>
        public ScanResult BuildCachedResult()
        {
            ScanResult result = new ScanResult();
            result.Time = DateTime.Now;
            List<string> keys = GetDeviceKeys();
            for (int i = 0; i < keys.Count; i++) AddCachedCard(result, keys[i]);
            return result;
        }

        private void AddCachedCard(ScanResult result, string key)
        {
            if (!IsDeviceEnabled(key)) return;

            bool isK02 = string.Equals(key, "K02BS", StringComparison.OrdinalIgnoreCase);
            bool isMchose = string.Equals(key, "MCHOSE", StringComparison.OrdinalIgnoreCase);
            bool isBluetooth = key.Length >= 3 && string.Equals(key.Substring(0, 3), "BT:", StringComparison.OrdinalIgnoreCase);

            RuntimeState st = GetRuntime(key);
            // 只有“上次关闭时还连着、而且最近一天内确实见过”的型号才恢复卡片。
            // 很久以前出现过的型号不再画成过期卡片（那就是用户说的“很早的卡片”），
            // 等首轮扫描真的发现它时再出现。MCHOSE / K02BS 是两个固定型号，始终保留。
            DateTime seen = GetLastSeenUtc(key);
            bool fresh = seen != DateTime.MinValue && (DateTime.UtcNow - seen) <= TimeSpan.FromHours(StaleCardHours);
            if (!isK02 && !isMchose && !(st.Connected && fresh)) return;

            bool hasLevel = st.HasLastLevel;
            string mode = string.IsNullOrEmpty(st.LastMode) ? (isK02 ? "双模" : "2.4G") : st.LastMode;
            Color theme = (isK02 || isBluetooth) ? Color.FromArgb(46, 144, 250) : Color.FromArgb(18, 183, 106);

            DeviceCardModel card = new DeviceCardModel();
            card.DeviceKey = key;
            card.Title = TitleOf(key);
            if (isK02) card.Subtitle = "VID 10D6 · PID 4801 · 2.4G / 蓝牙 游戏耳机";
            else if (isMchose) card.Subtitle = "VID 3837 · PID 100A · 迈从 2.4G 鼠标";
            else card.Subtitle = "";
            card.Mode = mode;
            card.ThemeColor = theme;
            card.Connected = st.Connected;
            card.HasLevel = hasLevel;
            if (hasLevel)
            {
                card.Level = st.LastLevel;
                card.LevelText = st.LastLevel + "%";
            }
            else card.LevelText = "—";
            card.Charging = false;
            card.StatusText = st.Connected ? "读取中" : "未连接";
            card.StatusLine = st.Connected
                ? mode + " · 上次状态，正在读取"
                : (hasLevel ? "上次电量 · 等待设备" : "等待设备");
            card.EstimateHint = hasLevel ? (st.Connected ? "上次电量" : "最后电量") : "";
            card.Detail = "启动时恢复上次保存的状态";
            card.Source = "上次状态";
            card.Accent = hasLevel && st.LastLevel < 30 ? Color.FromArgb(240, 68, 56) : theme;
            card.CanEstimate = isK02;
            card.EstimateEnabled = isK02 && GetEstimateConfig(key).Enabled;
            card.CanGpwSetup = IsGpwDeviceKey(key);
            result.Cards.Add(card);
        }

        /// <summary>GPW 三个型号级键（接收器 / 本体 / 有线），用于把“电压设置”入口恢复到卡片上。</summary>
        public static bool IsGpwDeviceKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            return string.Equals(key, "046D_C539", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "046D_4079", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "046D_C088", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 蓝牙阶梯估算。
        ///
        /// 规则：
        /// 1. 蓝牙上报值必须正好是某个已配置阶梯，否则判定“阶梯估算设置有误”；
        /// 2. 主显示值是时间推算值，但不会低于“下一档”，避免估算跑到下一档以下；
        /// 3. 蓝牙上报到新阶梯时重新计时；卡片小字显示反馈值与已保持时间；
        /// 4. 每一档的时长按五个固定权重区间分配（见 SegmentHours），权重不参与计时锚点签名。
        /// </summary>
        public int EstimateBt(string deviceKey, int reported, out double usedHours, out int next,
            out bool invalid, out string invalidReason, out int projected)
        {
            EstimateConfig cfg = GetEstimateConfig(deviceKey);
            usedHours = 0;
            next = 0;
            invalid = false;
            invalidReason = "";
            projected = reported;

            if (!IsValidStageSet(cfg, out invalidReason))
            {
                invalid = true;
                SetLastLevel(deviceKey, reported);
                return reported;
            }

            for (int i = 0; i < cfg.Stages.Count; i++)
            {
                if (cfg.Stages[i] < reported) { next = cfg.Stages[i]; break; }
            }
            if (next > reported) next = reported;

            if (!cfg.Stages.Contains(reported))
            {
                invalid = true;
                invalidReason = string.Format("蓝牙上报 {0}%，不在阶梯 {1} 中", reported, FormatStages(cfg.Stages));
                SetLastLevel(deviceKey, reported);
                return reported;
            }

            RuntimeState st = GetRuntime(deviceKey);
            string sig = ConfigSignature(cfg);
            bool resetAnchor = !st.HasBtStage || st.BtStage != reported || st.BtStageUtc == DateTime.MinValue
                || !string.Equals(st.BtConfigSig, sig, StringComparison.Ordinal);
            if (resetAnchor)
            {
                st.HasBtStage = true;
                st.BtStage = reported;
                st.BtStageUtc = DateTime.UtcNow;
                st.BtConfigSig = sig;
                st.HasLastLevel = true;
                st.LastLevel = reported;
                if (st.HoldFrozen) st.FrozenHeldHours = 0;
                Save();
            }

            TimeSpan elapsed;
            if (st.HoldFrozen)
                elapsed = TimeSpan.FromHours(Math.Max(0, st.FrozenHeldHours));
            else
                elapsed = DateTime.UtcNow - st.BtStageUtc;
            if (elapsed.TotalHours < 0) elapsed = TimeSpan.Zero;
            usedHours = elapsed.TotalHours;

            double segmentHours = SegmentHours(cfg, reported, next);
            if (segmentHours > 0)
            {
                double pct = reported - (elapsed.TotalHours / segmentHours) * (reported - next);
                if (pct < next) pct = next;
                if (pct > reported) pct = reported;
                projected = (int)Math.Round(pct);
                if (projected < 0) projected = 0;
                if (projected > 100) projected = 100;
            }

            // 主数字使用时间推算值，但已被限制在 [next, reported]。
            SetLastLevel(deviceKey, reported);
            return projected;
        }

        #region 开机自启

        /// <summary>
        /// 启动期自启入口审计的结论，界面据此在状态栏显示一行。
        /// Ok* 表示已有入口仍指向当前 exe，Repaired* 表示本次启动已静默重写。
        /// </summary>
        public enum AutoStartState
        {
            Disabled,
            NotManaged,
            OkTask,
            OkStartupFolder,
            OkVbs,
            OkRun,
            RepairedTask,
            RepairedStartupFolder,
            RepairedVbs,
            RepairedRun,
            Failed
        }

        /// <summary>
        /// 只有正式 exe 名才管理自启入口：用户把 exe 改名成调试副本放到别处运行时，
        /// 不去改写自启，免得把自启入口指到临时副本上。
        /// </summary>
        public static bool IsFinalExeName()
        {
            string name = Path.GetFileName(Application.ExecutablePath);
            return name.Equals("WirelessBattery.exe", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("无线设备电量", StringComparison.OrdinalIgnoreCase);
        }

        private static string StartupDir
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.Startup); }
        }

        private static string ShortcutPath
        {
            get { return Path.Combine(StartupDir, "设备电量.lnk"); }
        }

        private static string VbsPath
        {
            get { return Path.Combine(StartupDir, "设备电量.vbs"); }
        }

        public static string LauncherPath
        {
            get { return Path.Combine(Application.StartupPath, "启动设备电量.cmd"); }
        }

        private static string CmdPath
        {
            get
            {
                string comspec = Environment.GetEnvironmentVariable("ComSpec");
                if (!string.IsNullOrEmpty(comspec) && File.Exists(comspec)) return comspec;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            }
        }

        /// <summary>
        /// 生成启动脚本：先 Unblock-File 去掉下载标记，再由 cmd 的 start 启动 exe。
        /// 该脚本由快捷方式/计划任务调用，因此用户不需要直接双击带“网络来源”的 exe。
        /// </summary>
        public static bool EnsureLauncher()
        {
            try
            {
                string content = "@echo off\r\n" +
                    "setlocal\r\n" +
                    "cd /d \"%~dp0\"\r\n" +
                    "powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"Unblock-File -LiteralPath '%~dp0WirelessBattery.exe' -ErrorAction SilentlyContinue\"\r\n" +
                    "start \"\" \"%~dp0WirelessBattery.exe\" %*\r\n";
                File.WriteAllText(LauncherPath, content, Encoding.Default);
                return File.Exists(LauncherPath);
            }
            catch { return false; }
        }

        private static string CmdArguments(string extra)
        {
            string args = "/c \"\"" + LauncherPath + "\"\"";
            if (!string.IsNullOrEmpty(extra)) args += " " + extra;
            return args;
        }

        private static bool FileContainsText(string path, string needle)
        {
            try
            {
                if (!File.Exists(path) || string.IsNullOrEmpty(needle)) return false;
                byte[] raw = File.ReadAllBytes(path);
                string uni = Encoding.Unicode.GetString(raw);
                if (uni.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                string ansi = Encoding.Default.GetString(raw);
                return ansi.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        private static bool FileContainsExe(string path)
        {
            return FileContainsText(path, Application.ExecutablePath) || FileContainsText(path, LauncherPath);
        }

        private static int RunSchtasks(string arguments, out string output)
        {
            output = "";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");
                psi.Arguments = arguments;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    output = stdout + stderr;
                    if (!p.WaitForExit(15000))
                    {
                        try { p.Kill(); } catch { }
                        return -1;
                    }
                    return p.ExitCode;
                }
            }
            catch { return -1; }
        }

        private static bool TaskExists()
        {
            string output;
            return RunSchtasks("/Query /TN \"" + TaskName + "\"", out output) == 0;
        }

        private static bool TaskIsHighest()
        {
            string output;
            if (RunSchtasks("/Query /TN \"" + TaskName + "\" /XML", out output) != 0) return false;
            string compact = output.Replace("\0", "");
            return compact.IndexOf("<RunLevel>HighestAvailable</RunLevel>", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void DeleteTask()
        {
            string output;
            RunSchtasks("/Delete /TN \"" + TaskName + "\" /F", out output);
        }

        private static string CurrentUser
        {
            get
            {
                try { return WindowsIdentity.GetCurrent().Name; }
                catch { return Environment.UserDomainName + "\\" + Environment.UserName; }
            }
        }

        private static string BuildTaskXml()
        {
            string user = SecurityElement.Escape(CurrentUser);
            string dir = SecurityElement.Escape(Application.StartupPath);
            // 计划任务直接启动 exe，避免 cmd / start 的令牌降级。
            // 任务本身用 HighestAvailable，这样登录后就是管理员完整性，
            // 能向同样以管理员运行的 Explorer 注册托盘图标。
            string command = SecurityElement.Escape(Application.ExecutablePath);
            string arguments = "--tray";
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-16\"?>");
            sb.AppendLine("<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">");
            sb.AppendLine("  <RegistrationInfo>");
            sb.AppendLine("    <Author>WirelessBattery</Author>");
            sb.AppendLine("    <Description>设备电量：登录后自动在后台运行，并在托盘显示电量。</Description>");
            sb.AppendLine("  </RegistrationInfo>");
            sb.AppendLine("  <Triggers>");
            sb.AppendLine("    <LogonTrigger>");
            sb.AppendLine("      <Enabled>true</Enabled>");
            sb.AppendLine("      <UserId>" + user + "</UserId>");
            sb.AppendLine("      <Delay>PT3S</Delay>");
            sb.AppendLine("    </LogonTrigger>");
            sb.AppendLine("  </Triggers>");
            sb.AppendLine("  <Principals>");
            sb.AppendLine("    <Principal id=\"Author\">");
            sb.AppendLine("      <UserId>" + user + "</UserId>");
            sb.AppendLine("      <LogonType>InteractiveToken</LogonType>");
            sb.AppendLine("      <RunLevel>HighestAvailable</RunLevel>");
            sb.AppendLine("    </Principal>");
            sb.AppendLine("  </Principals>");
            sb.AppendLine("  <Settings>");
            sb.AppendLine("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>");
            sb.AppendLine("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>");
            sb.AppendLine("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>");
            sb.AppendLine("    <AllowHardTerminate>true</AllowHardTerminate>");
            sb.AppendLine("    <StartWhenAvailable>true</StartWhenAvailable>");
            sb.AppendLine("    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>");
            sb.AppendLine("    <IdleSettings>");
            sb.AppendLine("      <StopOnIdleEnd>false</StopOnIdleEnd>");
            sb.AppendLine("      <RestartOnIdle>false</RestartOnIdle>");
            sb.AppendLine("    </IdleSettings>");
            sb.AppendLine("    <AllowStartOnDemand>true</AllowStartOnDemand>");
            sb.AppendLine("    <Enabled>true</Enabled>");
            sb.AppendLine("    <Hidden>false</Hidden>");
            sb.AppendLine("    <RunOnlyIfIdle>false</RunOnlyIfIdle>");
            sb.AppendLine("    <WakeToRun>false</WakeToRun>");
            sb.AppendLine("    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>");
            sb.AppendLine("    <Priority>7</Priority>");
            sb.AppendLine("    <RestartOnFailure>");
            sb.AppendLine("      <Interval>PT1M</Interval>");
            sb.AppendLine("      <Count>3</Count>");
            sb.AppendLine("    </RestartOnFailure>");
            sb.AppendLine("  </Settings>");
            sb.AppendLine("  <Actions Context=\"Author\">");
            sb.AppendLine("    <Exec>");
            sb.AppendLine("      <Command>" + command + "</Command>");
            sb.AppendLine("      <Arguments>" + arguments + "</Arguments>");
            sb.AppendLine("      <WorkingDirectory>" + dir + "</WorkingDirectory>");
            sb.AppendLine("    </Exec>");
            sb.AppendLine("  </Actions>");
            sb.AppendLine("</Task>");
            return sb.ToString();
        }

        private bool CreateScheduledTask()
        {
            string temp = Path.Combine(Path.GetTempPath(), "WirelessBatteryTask_" + Process.GetCurrentProcess().Id + ".xml");
            try
            {
                EnsureLauncher();
                File.WriteAllText(temp, BuildTaskXml(), Encoding.Unicode);
                string output;
                int code = RunSchtasks("/Create /TN \"" + TaskName + "\" /XML \"" + temp + "\" /F", out output);
                if (code != 0) return false;
                return TaskExists();
            }
            catch { return false; }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        public bool IsAutoStartEnabled()
        {
            if (TaskExists()) return true;
            if (FileContainsExe(ShortcutPath)) return true;
            if (FileContainsExe(VbsPath)) return true;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) return false;
                    string v = k.GetValue(RunValue) as string;
                    if (string.IsNullOrEmpty(v)) return false;
                    return v.IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        private static bool SaveShortcut(string path, string target, string arguments, string workingDir, string description)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return false;
                object shell = Activator.CreateInstance(shellType);
                object shortcut = shellType.InvokeMember("CreateShortcut",
                    BindingFlags.InvokeMethod, null, shell, new object[] { path });
                Type st = shortcut.GetType();
                st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { target });
                st.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { arguments });
                st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { workingDir });
                st.InvokeMember("WindowStyle", BindingFlags.SetProperty, null, shortcut, new object[] { 7 });
                st.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { description });
                st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { Application.ExecutablePath + ",0" });
                st.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, new object[0]);
                return File.Exists(path);
            }
            catch { return false; }
        }

        /// <summary>
        /// 在程序目录创建“打开设备电量.lnk”。快捷方式指向系统自带的 cmd.exe，
        /// 由 启动设备电量.cmd 先 Unblock-File 再 start，从而避开资源管理器对下载 exe 的 SmartScreen 拦截。
        /// </summary>
        public static bool CreateOpenShortcut(string shortcutPath)
        {
            try
            {
                if (!EnsureLauncher()) return false;
                return SaveShortcut(shortcutPath, CmdPath, CmdArguments(""), Application.StartupPath, "设备电量");
            }
            catch { return false; }
        }

        private static bool CreateStartupShortcut()
        {
            try
            {
                string dir = StartupDir;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                if (!SaveShortcut(ShortcutPath, Application.ExecutablePath, "--tray", Application.StartupPath, "设备电量"))
                    return false;
                return FileContainsExe(ShortcutPath);
            }
            catch { return false; }
        }

        /// <summary>
        /// 经 cmd.exe 调 启动设备电量.cmd（先 Unblock-File 再 start），
        /// 避免资源管理器对下载的 exe 直接弹出 SmartScreen。快捷方式指向系统自带 cmd.exe。
        /// </summary>
        private static bool CreateLauncherStartupShortcut()
        {
            try
            {
                string dir = StartupDir;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                if (!EnsureLauncher()) return false;
                if (!SaveShortcut(ShortcutPath, CmdPath, CmdArguments("--tray"), Application.StartupPath, "设备电量"))
                    return false;
                return FileContainsExe(ShortcutPath);
            }
            catch { return false; }
        }

        private static bool CreateStartupVbs()
        {
            try
            {
                string dir = StartupDir;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string content = "Set sh = CreateObject(\"WScript.Shell\")\r\n" +
                    "sh.Run \"\"\"" + Application.ExecutablePath + "\"\" --tray\", 0, False\r\n";
                File.WriteAllText(VbsPath, content, new UnicodeEncoding(false, true));
                return FileContainsExe(VbsPath);
            }
            catch { return false; }
        }

        private static bool CreateRunKey()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (k == null) return false;
                    k.SetValue(RunValue, "\"" + Application.ExecutablePath + "\" --tray", RegistryValueKind.String);
                }
                return true;
            }
            catch { return false; }
        }

        private static void DeleteAutoStartEntries()
        {
            DeleteTask();
            try { if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath); } catch { }
            try { if (File.Exists(VbsPath)) File.Delete(VbsPath); } catch { }
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k != null) k.DeleteValue(RunValue, false);
                }
            }
            catch { }
        }

        public bool ApplyAutoStart(bool enabled)
        {
            AutoStartWanted = enabled;
            NeedsAutoStartSetup = false;
            if (!enabled)
            {
                DeleteAutoStartEntries();
                AutoStart = false;
                return true;
            }

            // 首选计划任务：登录后直接由任务计划程序启动，不经过资源管理器，
            // 因此不会触发 SmartScreen，也不依赖“启动”文件夹权限。
            DeleteAutoStartEntries();
            bool ok = CreateScheduledTask();
            if (!ok) ok = CreateLauncherStartupShortcut();
            if (!ok) ok = CreateStartupShortcut();
            if (!ok) ok = CreateStartupVbs();
            if (!ok) ok = CreateRunKey();
            AutoStart = ok;
            return ok;
        }

        /// <summary>
        /// 启动期审计：核对现有自启入口是否仍指向当前 exe（例如 exe 被移动过），
        /// 不匹配就按现有优先级顺序静默重写，绝不弹窗。
        /// 与 ApplyAutoStart 的关键差别是“先建新入口、成功后再清其它级别”，
        /// 避免先全删、再建失败时出现一个入口都没有的空窗。
        /// </summary>
        public AutoStartState AuditAutoStart()
        {
            if (!AutoStartWanted) return AutoStartState.Disabled;
            if (!IsFinalExeName()) return AutoStartState.NotManaged;

            bool taskExists;
            string command;
            bool taskHighest;
            QueryAutoStartTask(out taskExists, out command, out taskHighest);

            // 已提权时任务必须是 HighestAvailable：否则登录后任务以普通权限启动，
            // 又会回到“Explorer 权限更高、托盘注册失败”的老问题（沿用 Load 的判定）。
            bool taskOk = taskExists && TaskPointsAtUs(command)
                && (!Elevation.IsElevated() || taskHighest);
            if (taskOk) return KeepAutoStart(AutoStartState.OkTask);

            // 计划任务缺失或陈旧：按第一优先级重建它。
            if (CreateScheduledTask())
            {
                DeleteAutoStartEntriesExcept(true, false, false, false);
                return KeepAutoStart(AutoStartState.RepairedTask);
            }

            // 建不了计划任务（权限不足等）就退回下一个入口。
            // 已经指向当前 exe 的入口不重写，只清掉其它级别的残留，保证登录后只启动一次。
            if (ShortcutPointsAtUs(ShortcutPath))
            {
                DeleteAutoStartEntriesExcept(taskOk, true, false, false);
                return KeepAutoStart(AutoStartState.OkStartupFolder);
            }
            if (FileContainsExe(VbsPath))
            {
                DeleteAutoStartEntriesExcept(taskOk, false, true, false);
                return KeepAutoStart(AutoStartState.OkVbs);
            }
            if (RunEntryPointsAtUs())
            {
                DeleteAutoStartEntriesExcept(taskOk, false, false, true);
                return KeepAutoStart(AutoStartState.OkRun);
            }

            if (CreateLauncherStartupShortcut() || CreateStartupShortcut())
            {
                DeleteAutoStartEntriesExcept(taskOk, true, false, false);
                return KeepAutoStart(AutoStartState.RepairedStartupFolder);
            }
            if (CreateStartupVbs())
            {
                DeleteAutoStartEntriesExcept(taskOk, false, true, false);
                return KeepAutoStart(AutoStartState.RepairedVbs);
            }
            if (CreateRunKey())
            {
                DeleteAutoStartEntriesExcept(taskOk, false, false, true);
                return KeepAutoStart(AutoStartState.RepairedRun);
            }

            // 所有入口都写不进去：如实降级，等用户在设置里手动重试。
            AutoStart = false;
            return AutoStartState.Failed;
        }

        private AutoStartState KeepAutoStart(AutoStartState state)
        {
            AutoStart = true;
            NeedsAutoStartSetup = false;
            return state;
        }

        /// <summary>
        /// 读出计划任务的真实启动命令。schtasks /XML 的输出是宽字符，
        /// 先去掉 NUL（沿用 TaskIsHighest 的处理）再找标签内容。
        /// </summary>
        private static void QueryAutoStartTask(out bool exists, out string command, out bool highest)
        {
            exists = false;
            command = "";
            highest = false;
            string output;
            if (RunSchtasks("/Query /TN \"" + TaskName + "\" /XML", out output) != 0) return;
            exists = true;
            string xml = output.Replace("\0", "");
            highest = xml.IndexOf("<RunLevel>HighestAvailable</RunLevel>", StringComparison.OrdinalIgnoreCase) >= 0;
            command = ExtractXmlElement(xml, "Command");
        }

        private static string ExtractXmlElement(string xml, string tag)
        {
            if (string.IsNullOrEmpty(xml)) return "";
            string open = "<" + tag + ">";
            string close = "</" + tag + ">";
            int start = xml.IndexOf(open, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return "";
            start += open.Length;
            int end = xml.IndexOf(close, start, StringComparison.OrdinalIgnoreCase);
            if (end <= start) return "";
            string value = xml.Substring(start, end - start).Trim();
            return value.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
                .Replace("&quot;", "\"").Replace("&apos;", "'");
        }

        private static bool TaskPointsAtUs(string command)
        {
            return SamePath(command, Application.ExecutablePath) || SamePath(command, LauncherPath);
        }

        /// <summary>
        /// 判断启动文件夹快捷方式是否仍指向当前 exe。先做二进制文本包含匹配（FileContainsExe，
        /// 与 IsAutoStartEnabled 同源），再读一次快捷方式的真实目标：经“启动脚本”中转的快捷方式
        /// 里，中文路径不总能被文本搜索命中，只看文本会把有效入口误判成陈旧、并重写成直指 exe，
        /// 反而破坏了 SmartScreen 规避链路。
        /// </summary>
        private static bool ShortcutPointsAtUs(string path)
        {
            if (FileContainsExe(path)) return true;
            if (!File.Exists(path)) return false;
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return false;
                object shell = Activator.CreateInstance(shellType);
                object shortcut = shellType.InvokeMember("CreateShortcut",
                    BindingFlags.InvokeMethod, null, shell, new object[] { path });
                Type st = shortcut.GetType();
                string target = st.InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, new object[0]) as string;
                string arguments = st.InvokeMember("Arguments", BindingFlags.GetProperty, null, shortcut, new object[0]) as string;
                if (SamePath(target, Application.ExecutablePath) || SamePath(target, LauncherPath)) return true;
                if (!string.IsNullOrEmpty(arguments))
                {
                    if (arguments.IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                    if (arguments.IndexOf(LauncherPath, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
                return false;
            }
            catch { return false; }
        }

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            // schtasks 输出的是绝对路径，这里只做“去空白 + 去末尾反斜杠 + 忽略大小写”的归一化，
            // 不引入路径解析（相对路径不在本场景）。
            string x = TrimPathTail(a);
            string y = TrimPathTail(b);
            return string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
        }

        private static string TrimPathTail(string value)
        {
            string s = value.Trim();
            while (s.Length > 0 && s[s.Length - 1] == '\\') s = s.Substring(0, s.Length - 1);
            return s;
        }

        private static bool RunEntryPointsAtUs()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) return false;
                    string v = k.GetValue(RunValue) as string;
                    if (string.IsNullOrEmpty(v)) return false;
                    return v.IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0
                        || v.IndexOf(LauncherPath, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// 清掉除保留级别以外的自启入口，防止旧路径的残留入口在登录后再启动一次。
        /// 全部静默失败：清理不成功不影响本次启动。
        /// </summary>
        private static void DeleteAutoStartEntriesExcept(bool keepTask, bool keepShortcut, bool keepVbs, bool keepRun)
        {
            if (!keepTask) DeleteTask();
            if (!keepShortcut)
            {
                try { if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath); } catch { }
            }
            if (!keepVbs)
            {
                try { if (File.Exists(VbsPath)) File.Delete(VbsPath); } catch { }
            }
            if (!keepRun)
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                    {
                        if (k != null) k.DeleteValue(RunValue, false);
                    }
                }
                catch { }
            }
        }

        #endregion
    }
}
