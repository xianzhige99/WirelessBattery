using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace WirelessBattery
{
    /// <summary>
    /// settings.ini 的唯一入口：键名、读取、旧版迁移、防损坏写入都在这里。
    /// AppSettings 只保留业务校验与派生规则，不再直接拼键名或写文件。
    ///
    /// 文件格式保持 1.3 以来的平面键（旧版程序仍能读回），只在每个设备行后追加
    /// ShowCard / ShowTray / Order / LastSeen 四个可选键；旧配置缺这些键即按“跟随启用状态”补齐。
    /// </summary>
    internal static class ConfigStore
    {
        // ---- 全局键 ----
        internal const string GlobalInterval = "Interval";
        internal const string GlobalCloseToTray = "CloseToTray";
        internal const string GlobalAutoStartWanted = "AutoStartWanted";
        // K02BS：只有接收器在线时是否也算“已连接”。缺省 0（严格口径）。
        internal const string GlobalK02BSSoleDongle = "K02BSSoleDongleAsConnected";

        // ---- 分段前缀 ----
        private const string DevicePrefix = "Device.";
        private const string EstimatePrefix = "Estimate.";
        private const string RuntimePrefix = "Runtime.";
        private const string ProbePrefix = "Probe.";
        private const string GpwPrefix = "Gpw.";

        // 写盘串行化：后台扫描线程与 UI 线程都会调 Save()。
        private static readonly object WriteSync = new object();

        internal static string DeviceField(string id, string field)
        {
            return DevicePrefix + id + "." + field;
        }

        internal static string EstimateField(string id, string field)
        {
            return EstimatePrefix + id + "." + field;
        }

        internal static string RuntimeField(string id, string field)
        {
            return RuntimePrefix + id + "." + field;
        }

        internal static string ProbeField(string modelKey, string field)
        {
            return ProbePrefix + modelKey + "." + field;
        }

        internal static string GpwField(string field)
        {
            return GpwPrefix + field;
        }

        /// <summary>
        /// 稳定标识校验：只允许型号级标识（MCHOSE、K02BS、046D:C539、BT:设备名）。
        /// 含路径分隔符、#、花括号、VID_/PID_/MI_ 的键属于个体级标识，一律拒绝。
        /// </summary>
        internal static bool IsSafeDeviceId(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            string s = id.Trim();
            if (s.Length == 0 || s.Length > 96) return false;
            if (s.IndexOf('\\') >= 0) return false;
            if (s.IndexOf('/') >= 0) return false;
            if (s.IndexOf('#') >= 0) return false;
            if (s.IndexOf('{') >= 0 || s.IndexOf('}') >= 0) return false;
            if (s.IndexOf('\t') >= 0 || s.IndexOf('\r') >= 0 || s.IndexOf('\n') >= 0) return false;
            string upper = s.ToUpperInvariant();
            if (upper.IndexOf("VID_", StringComparison.Ordinal) >= 0) return false;
            if (upper.IndexOf("PID_", StringComparison.Ordinal) >= 0) return false;
            if (upper.IndexOf("MI_", StringComparison.Ordinal) >= 0) return false;
            return true;
        }

        /// <summary>
        /// 自定义设备名清洗：去掉控制字符（含换行/制表符，名称只允许单行）、首尾空白，
        /// 并按字符截断到 32 个。规则只放在这里，读写两侧共用。
        /// </summary>
        internal static string SanitizeName(string raw)
        {
            if (raw == null) return "";
            StringBuilder sb = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c < 32 || c == 127) continue;
                sb.Append(c);
            }
            string s = sb.ToString().Trim();
            if (s.Length > MaxDeviceNameLength) s = s.Substring(0, MaxDeviceNameLength);
            return s;
        }

        internal const int MaxDeviceNameLength = 32;

        // ==================================================================
        // 读
        // ==================================================================

        internal static void Load(string path, AppSettings s)
        {
            try
            {
                // 上次写到一半被杀进程留下的临时文件：删掉，正文始终以 settings.ini 为准。
                if (File.Exists(path + ".tmp")) TryDelete(path + ".tmp");
                if (File.Exists(path))
                {
                    string[] lines = File.ReadAllLines(path);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i].Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string key = line.Substring(0, eq).Trim();
                        string val = line.Substring(eq + 1).Trim();
                        Apply(s, key, val);
                    }
                }
            }
            catch { }

            MigrateDeviceFlags(s);
        }

        /// <summary>
        /// 旧配置迁移：没有 ShowCard/ShowTray 的型号跟随启用状态（勾选→显示，未勾选→不显示），
        /// 没有 Order 的按设置页现有顺序补位。已经是新格式的行原样保留。
        /// </summary>
        private static void MigrateDeviceFlags(AppSettings s)
        {
            try
            {
                List<string> keys = s.GetDeviceKeys();
                foreach (KeyValuePair<string, bool> kv in s.EnabledDevices)
                {
                    if (!ContainsIgnore(keys, kv.Key)) keys.Add(kv.Key);
                }
                for (int i = 0; i < keys.Count; i++)
                {
                    string id = keys[i];
                    if (!IsSafeDeviceId(id)) continue;
                    bool enabled = s.IsDeviceEnabled(id);
                    if (!s.DeviceShowCard.ContainsKey(id)) s.DeviceShowCard[id] = enabled;
                    if (!s.DeviceShowTray.ContainsKey(id)) s.DeviceShowTray[id] = enabled;
                    if (!s.DeviceOrder.ContainsKey(id)) s.DeviceOrder[id] = i;
                }
            }
            catch { }
        }

        private static bool ContainsIgnore(List<string> list, string key)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], key, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static void Apply(AppSettings s, string key, string val)
        {
            if (key.Equals(GlobalInterval, StringComparison.OrdinalIgnoreCase))
            {
                int v;
                if (int.TryParse(val, out v) && v >= 0 && v <= 3600) s.IntervalSeconds = v;
                return;
            }
            if (key.Equals(GlobalCloseToTray, StringComparison.OrdinalIgnoreCase))
            {
                s.CloseToTray = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                return;
            }
            if (key.Equals(GlobalAutoStartWanted, StringComparison.OrdinalIgnoreCase))
            {
                s.AutoStartWanted = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                return;
            }
            if (key.Equals(GlobalK02BSSoleDongle, StringComparison.OrdinalIgnoreCase))
            {
                s.K02BSSoleDongleAsConnected = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                return;
            }
            // 兼容 1.2 的旧键，读取但不再使用。
            if (key.Equals("BtEstimate", StringComparison.OrdinalIgnoreCase)) return;
            if (key.StartsWith("FullHours.", StringComparison.OrdinalIgnoreCase)) return;

            // 型号级协议路由缓存。旧配置里没有这些键也能正常加载；未知字段忽略。
            if (key.StartsWith(ProbePrefix, StringComparison.OrdinalIgnoreCase))
            {
                string rest = key.Substring(ProbePrefix.Length);
                int dot = rest.LastIndexOf('.');
                if (dot <= 0) return;
                string model = rest.Substring(0, dot);
                string field = rest.Substring(dot + 1);
                ProbeRecord rec;
                if (!s.Probes.TryGetValue(model, out rec))
                {
                    rec = new ProbeRecord();
                    s.Probes[model] = rec;
                }
                if (field.Equals("Proto", StringComparison.OrdinalIgnoreCase))
                    rec.Protocol = ProtocolRouter.ParseProtocol(val);
                else if (field.Equals("Type", StringComparison.OrdinalIgnoreCase))
                    rec.ValueKind = ProtocolRouter.ParseValueKind(val);
                else if (field.Equals("Feature", StringComparison.OrdinalIgnoreCase))
                {
                    int v;
                    if (int.TryParse(val, out v)) rec.FeatureId = v;
                }
                else if (field.Equals("Ts", StringComparison.OrdinalIgnoreCase))
                {
                    DateTime dt;
                    if (DateTime.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out dt))
                        rec.StampUtc = dt;
                }
                else if (field.Equals("Until", StringComparison.OrdinalIgnoreCase))
                {
                    DateTime dt;
                    if (DateTime.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out dt))
                        rec.UntilUtc = dt;
                }
                else if (field.Equals("Fails", StringComparison.OrdinalIgnoreCase))
                {
                    int v;
                    if (int.TryParse(val, out v)) rec.Fails = v;
                }
                else if (field.Equals("Ver", StringComparison.OrdinalIgnoreCase))
                {
                    int v;
                    if (int.TryParse(val, out v)) rec.Firmware = v;
                }
                else if (field.Equals("Dev", StringComparison.OrdinalIgnoreCase))
                {
                    int v;
                    if (int.TryParse(val, out v) && v >= 0 && v <= 6) rec.DeviceIndex = v == 0 ? -1 : v;
                }
                return;
            }

            // GPW 电压校准参数。旧配置没有这些键也能正常加载；单行越界只忽略该行，不整块清除。
            if (key.StartsWith(GpwPrefix, StringComparison.OrdinalIgnoreCase))
            {
                ApplyGpwField(s, key.Substring(GpwPrefix.Length), val);
                return;
            }

            if (key.StartsWith(DevicePrefix, StringComparison.OrdinalIgnoreCase))
            {
                string rest = key.Substring(DevicePrefix.Length);
                int dot = rest.LastIndexOf('.');
                if (dot <= 0) return;
                string dev = rest.Substring(0, dot);
                string field = rest.Substring(dot + 1);
                // 个体级标识不允许进入配置：宁可丢这一行，也不让实例路径/端口号成为识别依据。
                if (!IsSafeDeviceId(dev)) return;
                if (field.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
                    s.EnabledDevices[dev] = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                else if (field.Equals("Seen", StringComparison.OrdinalIgnoreCase))
                {
                    if (val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase)) s.SeedDiscovered(dev);
                }
                else if (field.Equals("ShowCard", StringComparison.OrdinalIgnoreCase))
                    s.DeviceShowCard[dev] = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                else if (field.Equals("ShowTray", StringComparison.OrdinalIgnoreCase))
                    s.DeviceShowTray[dev] = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                else if (field.Equals("Order", StringComparison.OrdinalIgnoreCase))
                {
                    int o;
                    if (int.TryParse(val, out o) && o >= 0) s.DeviceOrder[dev] = o;
                }
                else if (field.Equals("Name", StringComparison.OrdinalIgnoreCase))
                {
                    // 自定义名称：为空表示“用识别到的默认名”，因此空值不落条目。
                    string name = SanitizeName(val);
                    if (name.Length > 0) s.DeviceNames[dev] = name;
                }
                else if (field.Equals("LastSeen", StringComparison.OrdinalIgnoreCase))
                {
                    DateTime dt;
                    if (DateTime.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out dt))
                        s.DeviceLastSeen[dev] = dt;
                }
                return;
            }

            if (key.StartsWith(EstimatePrefix, StringComparison.OrdinalIgnoreCase))
            {
                string rest = key.Substring(EstimatePrefix.Length);
                int dot = rest.LastIndexOf('.');
                if (dot <= 0) return;
                string dev = rest.Substring(0, dot);
                string field = rest.Substring(dot + 1);
                EstimateConfig cfg;
                if (!s.Estimates.TryGetValue(dev, out cfg))
                {
                    cfg = new EstimateConfig();
                    cfg.Enabled = dev.Equals("K02BS", StringComparison.OrdinalIgnoreCase);
                    cfg.FullHours = dev.Equals("K02BS", StringComparison.OrdinalIgnoreCase) ? 163.0 : 100.0;
                    s.Estimates[dev] = cfg;
                }
                if (field.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
                    cfg.Enabled = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                else if (field.Equals("Hours", StringComparison.OrdinalIgnoreCase))
                {
                    double h;
                    if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out h) && h > 0) cfg.FullHours = h;
                }
                else if (field.Equals("Stages", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = val.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    List<int> stages = new List<int>();
                    for (int i = 0; i < parts.Length; i++)
                    {
                        int v;
                        if (int.TryParse(parts[i].Trim(), out v) && v >= 0 && v <= 100 && !stages.Contains(v)) stages.Add(v);
                    }
                    if (stages.Count >= 2)
                    {
                        stages.Sort(delegate (int a, int b) { return b.CompareTo(a); });
                        cfg.Stages = stages;
                    }
                }
                else if (field.Equals("Weights", StringComparison.OrdinalIgnoreCase))
                {
                    // 缺这个键（旧配置）就用默认权重；写了但不合法也退回默认，不因一行坏配置把估算整坏。
                    List<double> weights = AppSettings.ParseWeights(val);
                    if (weights != null) cfg.Weights = weights;
                }
                return;
            }

            if (key.StartsWith(RuntimePrefix, StringComparison.OrdinalIgnoreCase))
            {
                string rest = key.Substring(RuntimePrefix.Length);
                int dot = rest.LastIndexOf('.');
                if (dot <= 0) return;
                string dev = rest.Substring(0, dot);
                string field = rest.Substring(dot + 1);
                RuntimeState st;
                if (!s.Runtime.TryGetValue(dev, out st)) { st = new RuntimeState(); s.Runtime[dev] = st; }
                if (field.Equals("Connected", StringComparison.OrdinalIgnoreCase))
                    st.Connected = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                else if (field.Equals("Since", StringComparison.OrdinalIgnoreCase))
                {
                    DateTime dt;
                    if (DateTime.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out dt))
                        st.SinceUtc = dt;
                }
                else if (field.Equals("Level", StringComparison.OrdinalIgnoreCase))
                {
                    int lv;
                    if (int.TryParse(val, out lv))
                    {
                        st.LastLevel = lv;
                        st.HasLastLevel = true;
                    }
                }
                else if (field.Equals("HasLast", StringComparison.OrdinalIgnoreCase))
                {
                    st.HasLastLevel = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                }
                else if (field.Equals("BtStage", StringComparison.OrdinalIgnoreCase))
                {
                    int s2;
                    if (int.TryParse(val, out s2)) st.BtStage = s2;
                }
                else if (field.Equals("BtStageSince", StringComparison.OrdinalIgnoreCase))
                {
                    DateTime dt;
                    if (DateTime.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out dt))
                        st.BtStageUtc = dt;
                }
                else if (field.Equals("HasBtStage", StringComparison.OrdinalIgnoreCase))
                {
                    st.HasBtStage = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                }
                else if (field.Equals("BtConfigSig", StringComparison.OrdinalIgnoreCase))
                {
                    st.BtConfigSig = val;
                }
                else if (field.Equals("DisconnectedSince", StringComparison.OrdinalIgnoreCase))
                {
                    DateTime dt;
                    if (DateTime.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out dt))
                        st.DisconnectedSinceUtc = dt;
                }
                else if (field.Equals("Paused", StringComparison.OrdinalIgnoreCase))
                {
                    DateTime dt;
                    if (DateTime.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out dt))
                        st.PausedUtc = dt;
                }
                else if (field.Equals("LastMode", StringComparison.OrdinalIgnoreCase)) st.LastMode = val;
                else if (field.Equals("ModeSince", StringComparison.OrdinalIgnoreCase))
                {
                    DateTime dt;
                    if (DateTime.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out dt))
                        st.ModeSinceUtc = dt;
                }
                else if (field.Equals("ModeCheckDone", StringComparison.OrdinalIgnoreCase))
                    st.ModeCheckDone = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                else if (field.Equals("Frozen", StringComparison.OrdinalIgnoreCase))
                    st.Frozen = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                else if (field.Equals("HoldFrozen", StringComparison.OrdinalIgnoreCase))
                    st.HoldFrozen = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                else if (field.Equals("FrozenHas", StringComparison.OrdinalIgnoreCase))
                    st.FrozenHas = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                else if (field.Equals("FrozenLevel", StringComparison.OrdinalIgnoreCase))
                { int v; if (int.TryParse(val, out v)) st.FrozenLevel = v; }
                else if (field.Equals("FrozenReported", StringComparison.OrdinalIgnoreCase))
                { int v; if (int.TryParse(val, out v)) st.FrozenReported = v; }
                else if (field.Equals("FrozenHeldHours", StringComparison.OrdinalIgnoreCase))
                { double v; if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) st.FrozenHeldHours = v; }
                else if (field.Equals("HasLastDual", StringComparison.OrdinalIgnoreCase))
                    st.HasLastDual = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                else if (field.Equals("LastDualLevel", StringComparison.OrdinalIgnoreCase))
                { int v; if (int.TryParse(val, out v)) st.LastDualLevel = v; }
                else if (field.Equals("LastDualReported", StringComparison.OrdinalIgnoreCase))
                { int v; if (int.TryParse(val, out v)) st.LastDualReported = v; }
                else if (field.Equals("LastDualHeldHours", StringComparison.OrdinalIgnoreCase))
                { double v; if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) st.LastDualHeldHours = v; }
            }
        }

        private static void ApplyGpwField(AppSettings s, string field, string val)
        {
            GpwConfig c = s.GetGpwConfig();
            if (field.Equals("FullMv", StringComparison.OrdinalIgnoreCase))
            {
                double v;
                if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v >= 3300 && v <= 4500) c.FullMv = v;
            }
            else if (field.Equals("EmptyMv", StringComparison.OrdinalIgnoreCase))
            {
                double v;
                if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v >= 3000 && v <= 4500) c.EmptyMv = v;
            }
            else if (field.Equals("MidOffset", StringComparison.OrdinalIgnoreCase))
            {
                double v;
                if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v >= -10 && v <= 10) c.MidOffset = v;
            }
            else if (field.Equals("MedianWindow", StringComparison.OrdinalIgnoreCase))
            {
                int v;
                if (int.TryParse(val, out v) && v >= 1 && v <= 7) c.MedianWindow = v;
            }
            else if (field.Equals("Alpha", StringComparison.OrdinalIgnoreCase))
            {
                double v;
                if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v >= 0.1 && v <= 0.5) c.Alpha = v;
            }
            else if (field.Equals("Hysteresis", StringComparison.OrdinalIgnoreCase))
            {
                double v;
                if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v >= 0 && v <= 20) c.Hysteresis = v;
            }
            else if (field.Equals("Rise", StringComparison.OrdinalIgnoreCase))
            {
                double v;
                if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v >= 0 && v <= 500) c.RiseTolerance = v;
            }
            else if (field.Equals("AutoAdjust", StringComparison.OrdinalIgnoreCase))
            {
                c.AutoAdjust = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            else if (field.Equals("ObservedMax", StringComparison.OrdinalIgnoreCase))
            {
                double v;
                if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v >= 0 && v <= 6000) c.ObservedMaxMv = v;
            }
            else if (field.Equals("LowMv", StringComparison.OrdinalIgnoreCase))
            {
                // 历史字段保留解析，不再使用。
            }
            else if (field.Equals("Samples", StringComparison.OrdinalIgnoreCase))
            {
                int v;
                if (int.TryParse(val, out v) && v >= 0) c.Samples = v;
            }
            else if (field.Equals("Cycles", StringComparison.OrdinalIgnoreCase))
            {
                int v;
                if (int.TryParse(val, out v) && v >= 0) c.CycleCount = v;
            }
            else if (field.Equals("LastAutoCycle", StringComparison.OrdinalIgnoreCase))
            {
                int v;
                if (int.TryParse(val, out v) && v >= 0) c.LastAutoCycle = v;
            }
            else if (field.Equals("LastAutoAdjust", StringComparison.OrdinalIgnoreCase))
            {
                DateTime dt;
                if (DateTime.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out dt))
                    c.LastAutoAdjustUtc = dt;
            }
            else if (field.Equals("ManualEdited", StringComparison.OrdinalIgnoreCase))
            {
                c.ManualEdited = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
        }

        // ==================================================================
        // 写
        // ==================================================================

        internal static bool Save(string path, AppSettings s)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                List<string> lines = BuildLines(s);
                lock (WriteSync)
                {
                    return WriteAtomic(path, lines.ToArray());
                }
            }
            catch { return false; }
        }

        private static List<string> BuildLines(AppSettings s)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            List<string> lines = new List<string>();
            lines.Add("# WirelessBattery settings");
            lines.Add(GlobalInterval + "=" + s.IntervalSeconds.ToString(inv));
            lines.Add(GlobalCloseToTray + "=" + (s.CloseToTray ? "1" : "0"));
            lines.Add(GlobalAutoStartWanted + "=" + (s.AutoStartWanted ? "1" : "0"));
            lines.Add(GlobalK02BSSoleDongle + "=" + (s.K02BSSoleDongleAsConnected ? "1" : "0"));
            List<string> deviceKeys = s.GetDeviceKeys();
            for (int i = 0; i < deviceKeys.Count; i++)
            {
                string dev = deviceKeys[i];
                bool enabled = s.IsDeviceEnabled(dev);
                lines.Add(DeviceField(dev, "Enabled") + "=" + (enabled ? "1" : "0"));
                lines.Add(DeviceField(dev, "Seen") + "=1");
                if (!IsSafeDeviceId(dev)) continue;
                bool showCard;
                if (!s.DeviceShowCard.TryGetValue(dev, out showCard)) showCard = enabled;
                bool showTray;
                if (!s.DeviceShowTray.TryGetValue(dev, out showTray)) showTray = enabled;
                int order;
                if (!s.DeviceOrder.TryGetValue(dev, out order)) order = i;
                lines.Add(DeviceField(dev, "ShowCard") + "=" + (showCard ? "1" : "0"));
                lines.Add(DeviceField(dev, "ShowTray") + "=" + (showTray ? "1" : "0"));
                lines.Add(DeviceField(dev, "Order") + "=" + order.ToString(inv));
                string customName;
                if (s.DeviceNames.TryGetValue(dev, out customName) && !string.IsNullOrEmpty(customName))
                    lines.Add(DeviceField(dev, "Name") + "=" + customName);
                DateTime seen;
                if (s.DeviceLastSeen.TryGetValue(dev, out seen))
                    lines.Add(DeviceField(dev, "LastSeen") + "=" + seen.ToUniversalTime().ToString("o", inv));
            }
            foreach (KeyValuePair<string, EstimateConfig> kv in s.Estimates)
            {
                EstimateConfig c = kv.Value;
                lines.Add(EstimateField(kv.Key, "Enabled") + "=" + (c.Enabled ? "1" : "0"));
                lines.Add(EstimateField(kv.Key, "Hours") + "=" + c.FullHours.ToString("0.###", inv));
                lines.Add(EstimateField(kv.Key, "Stages") + "=" + AppSettings.FormatStages(c.Stages));
                lines.Add(EstimateField(kv.Key, "Weights") + "=" + AppSettings.FormatWeights(c.Weights));
            }
            foreach (KeyValuePair<string, RuntimeState> kv in s.Runtime)
            {
                RuntimeState st = kv.Value;
                lines.Add(RuntimeField(kv.Key, "Connected") + "=" + (st.Connected ? "1" : "0"));
                lines.Add(RuntimeField(kv.Key, "Since") + "=" + st.SinceUtc.ToString("o", inv));
                lines.Add(RuntimeField(kv.Key, "Level") + "=" + st.LastLevel.ToString(inv));
                lines.Add(RuntimeField(kv.Key, "HasLast") + "=" + (st.HasLastLevel ? "1" : "0"));
                lines.Add(RuntimeField(kv.Key, "HasBtStage") + "=" + (st.HasBtStage ? "1" : "0"));
                lines.Add(RuntimeField(kv.Key, "BtStage") + "=" + st.BtStage.ToString(inv));
                lines.Add(RuntimeField(kv.Key, "BtStageSince") + "=" + st.BtStageUtc.ToString("o", inv));
                lines.Add(RuntimeField(kv.Key, "BtConfigSig") + "=" + st.BtConfigSig);
                lines.Add(RuntimeField(kv.Key, "DisconnectedSince") + "=" + st.DisconnectedSinceUtc.ToString("o", inv));
                lines.Add(RuntimeField(kv.Key, "Paused") + "=" + st.PausedUtc.ToString("o", inv));
                lines.Add(RuntimeField(kv.Key, "LastMode") + "=" + st.LastMode);
                lines.Add(RuntimeField(kv.Key, "ModeSince") + "=" + st.ModeSinceUtc.ToString("o", inv));
                lines.Add(RuntimeField(kv.Key, "ModeCheckDone") + "=" + (st.ModeCheckDone ? "1" : "0"));
                lines.Add(RuntimeField(kv.Key, "Frozen") + "=" + (st.Frozen ? "1" : "0"));
                lines.Add(RuntimeField(kv.Key, "HoldFrozen") + "=" + (st.HoldFrozen ? "1" : "0"));
                lines.Add(RuntimeField(kv.Key, "FrozenHas") + "=" + (st.FrozenHas ? "1" : "0"));
                lines.Add(RuntimeField(kv.Key, "FrozenLevel") + "=" + st.FrozenLevel.ToString(inv));
                lines.Add(RuntimeField(kv.Key, "FrozenReported") + "=" + st.FrozenReported.ToString(inv));
                lines.Add(RuntimeField(kv.Key, "FrozenHeldHours") + "=" + st.FrozenHeldHours.ToString("0.###", inv));
                lines.Add(RuntimeField(kv.Key, "HasLastDual") + "=" + (st.HasLastDual ? "1" : "0"));
                lines.Add(RuntimeField(kv.Key, "LastDualLevel") + "=" + st.LastDualLevel.ToString(inv));
                lines.Add(RuntimeField(kv.Key, "LastDualReported") + "=" + st.LastDualReported.ToString(inv));
                lines.Add(RuntimeField(kv.Key, "LastDualHeldHours") + "=" + st.LastDualHeldHours.ToString("0.###", inv));
            }
            foreach (KeyValuePair<string, ProbeRecord> kv in s.Probes)
            {
                ProbeRecord pr = kv.Value;
                if (pr == null) continue;
                lines.Add(ProbeField(kv.Key, "Proto") + "=" + ProtocolRouter.ProtocolName(pr.Protocol));
                lines.Add(ProbeField(kv.Key, "Type") + "=" + ProtocolRouter.ValueKindName(pr.ValueKind));
                lines.Add(ProbeField(kv.Key, "Feature") + "=" + pr.FeatureId.ToString(inv));
                lines.Add(ProbeField(kv.Key, "Ts") + "=" + pr.StampUtc.ToString("o", inv));
                lines.Add(ProbeField(kv.Key, "Until") + "=" + pr.UntilUtc.ToString("o", inv));
                lines.Add(ProbeField(kv.Key, "Fails") + "=" + pr.Fails.ToString(inv));
                lines.Add(ProbeField(kv.Key, "Ver") + "=" + pr.Firmware.ToString(inv));
            if (pr.DeviceIndex > 0) lines.Add(ProbeField(kv.Key, "Dev") + "=" + pr.DeviceIndex.ToString(inv));
            }
            GpwConfig gpw = s.GetGpwConfig();
            lines.Add(GpwField("FullMv") + "=" + gpw.FullMv.ToString("0.###", inv));
            lines.Add(GpwField("EmptyMv") + "=" + gpw.EmptyMv.ToString("0.###", inv));
            lines.Add(GpwField("MidOffset") + "=" + gpw.MidOffset.ToString("0.###", inv));
            lines.Add(GpwField("MedianWindow") + "=" + gpw.MedianWindow.ToString(inv));
            lines.Add(GpwField("Alpha") + "=" + gpw.Alpha.ToString("0.###", inv));
            lines.Add(GpwField("Hysteresis") + "=" + gpw.Hysteresis.ToString("0.###", inv));
            lines.Add(GpwField("Rise") + "=" + gpw.RiseTolerance.ToString("0.###", inv));
            lines.Add(GpwField("AutoAdjust") + "=" + (gpw.AutoAdjust ? "1" : "0"));
            lines.Add(GpwField("ObservedMax") + "=" + gpw.ObservedMaxMv.ToString("0.###", inv));
            lines.Add(GpwField("Samples") + "=" + gpw.Samples.ToString(inv));
            lines.Add(GpwField("Cycles") + "=" + gpw.CycleCount.ToString(inv));
            lines.Add(GpwField("LastAutoCycle") + "=" + gpw.LastAutoCycle.ToString(inv));
            lines.Add(GpwField("LastAutoAdjust") + "=" + gpw.LastAutoAdjustUtc.ToString("o", inv));
            lines.Add(GpwField("ManualEdited") + "=" + (gpw.ManualEdited ? "1" : "0"));
            return lines;
        }

        /// <summary>
        /// 先写同目录临时文件再替换，覆盖三种场景：
        /// 目标已存在（File.Replace，失败则清只读重试、再失败退回覆盖写）、目标不存在（File.Move）。
        /// 任一步失败都删临时文件、保留原文件，绝不留下半截 settings.ini。
        /// </summary>
        private static bool WriteAtomic(string path, string[] lines)
        {
            string tmp = path + ".tmp";
            try
            {
                File.WriteAllLines(tmp, lines, Encoding.UTF8);
            }
            catch
            {
                TryDelete(tmp);
                return false;
            }

            if (!File.Exists(path))
            {
                try
                {
                    File.Move(tmp, path);
                    return true;
                }
                catch
                {
                    TryDelete(tmp);
                    return false;
                }
            }

            try
            {
                File.Replace(tmp, path, null);
                return true;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (PlatformNotSupportedException) { }

            // 目标只读：清只读属性后再试一次原子替换。
            try
            {
                FileAttributes attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            }
            catch { }

            try
            {
                File.Replace(tmp, path, null);
                return true;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (PlatformNotSupportedException) { }

            // 非 NTFS / 跨卷 / 被占用：退回覆盖写。不是原子替换，但整块内容一次写满，不会留半截。
            try
            {
                File.Copy(tmp, path, true);
                TryDelete(tmp);
                return true;
            }
            catch
            {
                TryDelete(tmp);
                return false;
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }
}
