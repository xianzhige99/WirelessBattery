using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Threading;

namespace WirelessBattery
{
    public enum DeviceKind
    {
        Mchose,
        K02BS,
        Jieli,
        Logitech,
        Generic,
        Unknown
    }

    public class HidGroup
    {
        public DeviceKind Kind;
        public ushort Vid;
        public ushort Pid;
        public string Product = "";
        public string Manufacturer = "";
        public List<HidInterfaceInfo> Interfaces = new List<HidInterfaceInfo>();

        public string Key
        {
            get { return string.Format("{0:X4}:{1:X4}|{2}", Vid, Pid, Product); }
        }
    }

    public class BatteryResult
    {
        public bool Ok;
        public int? Level;
        public bool? Charging;
        public string Source = "";
        public string Detail = "";
        public string Raw = "";
        public bool Unsupported;
        // 电压特征命中：只保留原始毫伏，换算不在本模块做。
        public bool HasVoltage;
        public int VoltageMv;

        public static BatteryResult Success(int level, bool? charging, string source, string detail, string raw)
        {
            BatteryResult r = new BatteryResult();
            r.Ok = true;
            r.Level = level;
            r.Charging = charging;
            r.Source = source;
            r.Detail = detail;
            r.Raw = raw;
            return r;
        }

        public static BatteryResult Fail(string source, string detail, string raw)
        {
            BatteryResult r = new BatteryResult();
            r.Ok = false;
            r.Source = source;
            r.Detail = detail;
            r.Raw = raw;
            return r;
        }

        public static BatteryResult NotSupported(string source, string detail, string raw)
        {
            BatteryResult r = Fail(source, detail, raw);
            r.Unsupported = true;
            return r;
        }
    }

    public class DeviceCardModel
    {
        public string Title = "";
        public string Subtitle = "";
        public string LevelText = "—";
        public int? Level;
        public string StatusText = "";
        public string Detail = "";
        public string Source = "";
        public Color Accent = Color.FromArgb(79, 70, 229);
        public Color ThemeColor = Color.FromArgb(79, 70, 229);
        public bool HasLevel;
        public bool Charging;
        public bool IsError;
        public string Raw = "";
        public HidGroup Group;

        public string DeviceKey = "";
        public string Mode = "";
        public bool Connected;
        public bool LinkPending;
        public bool IsEstimated;
        public bool CanEstimate;
        public bool EstimateEnabled;
        public bool EstimateInvalid;
        public string EstimateWarning = "";
        public string EstimateHint = "";
        public string StatusLine = "";
        // 路由层判断为“该型号明确没有电池特性”（不是超时）：用于保持改前的“不显示无电量设备”。
        public bool RouteUnsupported;
        // 罗技（HID++ 电压路径）设备：卡片上提供“电压设置”入口。
        public bool CanGpwSetup;
    }

    public class ScanResult
    {
        public List<DeviceCardModel> Cards = new List<DeviceCardModel>();
        public List<HidInterfaceInfo> Interfaces = new List<HidInterfaceInfo>();
        public DateTime Time = DateTime.Now;
        public bool PendingDisconnect;
        public int RecheckAfterMs;
    }

    public class BatteryService
    {
        public Action<string> Log;
        public AppSettings Settings;
        private int _recheckMs;
        private readonly ProtocolRouter _router;
        private bool _discoveredChanged;
        // GPW 滤波状态：按设备键存内存，重启后从新采样重新积累。
        private readonly Dictionary<string, GpwFilterState> _gpwStates = new Dictionary<string, GpwFilterState>(StringComparer.OrdinalIgnoreCase);
        private bool _gpwPersistPending;
        private bool _linkMonitorPending;
        private static volatile bool _scanActive;
        private HidGroup _linkGroup;

        // ---- 罗技接收器被动监听 ----
        // 这颗接收器会主动推帧：鼠标活动时推状态帧，电压变化时推 0x1001 通知。
        // 监听线程只读不写，用来回答两件事：链路最近有没有活动、有没有顺带送来的电压。
        private readonly object _linkSync = new object();
        private HidInterfaceInfo _linkIface;
        private int _linkFeatureIndex;
        private DateTime _linkLastTrafficUtc = DateTime.MinValue;
        private int _linkPassiveMv;
        private DateTime _linkPassiveUtc = DateTime.MinValue;
        private Thread _linkThread;
        private volatile bool _linkStop;

        /// <summary>
        /// 主动请求期间让监听线程暂停读的时间点（UTC ticks）。HID++ 应答只会送到正在读的那个句柄，
        /// 两边同时读就会互相抢应答；请求方在发报文前把这里推到 1.5 秒后，监听让开。
        /// </summary>
        internal static long LinkPauseUntilTicks;

        /// <summary>最近一次收到该接收器任何 HID++ 帧的时间（UTC）。</summary>
        public DateTime LinkLastActivityUtc
        {
            get { lock (_linkSync) { return _linkLastTrafficUtc; } }
        }

        /// <summary>退出前调用：通知监听线程收工（不强制中止）。</summary>
        public void StopLinkMonitor()
        {
            _linkStop = true;
        }

        /// <summary>
        /// 取设备主动推送的电压（90 秒内有效）。命中时本轮完全不发请求。
        /// 0x1001 通知格式：序号 2 = 特征索引、3 = 0x02、4..5 = 毫伏（大端）。
        /// </summary>
        public bool TryGetPassiveVoltage(out int mv)
        {
            lock (_linkSync)
            {
                if (_linkPassiveMv > 0 && (DateTime.UtcNow - _linkPassiveUtc).TotalSeconds <= 90.0)
                {
                    mv = _linkPassiveMv;
                    return true;
                }
            }
            mv = 0;
            return false;
        }

        private void EnsureLinkMonitor(HidGroup g)
        {
            HidInterfaceInfo best = null;
            for (int i = 0; i < g.Interfaces.Count; i++)
            {
                HidInterfaceInfo it = g.Interfaces[i];
                if (it.OutputLen == 20) { best = it; break; }
                if (best == null && it.OutputLen >= 7) best = it;
            }
            if (best == null) return;

            int featureIndex = -1;
            ProbeRecord rec = _router.Find(ProtocolRouter.ModelKeyOf(g));
            if (rec != null && rec.Protocol == ProtocolKind.LogitechVoltage) featureIndex = rec.FeatureId;

            lock (_linkSync)
            {
                _linkIface = best;
                if (featureIndex > 0) _linkFeatureIndex = featureIndex;
                if (_linkThread != null) return;
                // 还不知道 0x1001 的特性索引时先不读：这时监听拿到的帧只会被丢掉，
                // 但会把探测请求的应答一起抢走（应答只送到正在读的那个句柄）。
                if (_linkFeatureIndex <= 0) return;
                _linkStop = false;
                _linkThread = new Thread(LinkLoop);
                _linkThread.IsBackground = true;
                _linkThread.Name = "GpwLinkMonitor";
                _linkThread.Start();
            }
        }

        private void LinkLoop()
        {
            while (!_linkStop)
            {
                HidInterfaceInfo iface;
                int featureIndex;
                lock (_linkSync) { iface = _linkIface; featureIndex = _linkFeatureIndex; }
                if (iface == null) { Thread.Sleep(500); continue; }

                // HID++ 的应答只会被“同时读着这个设备的那一个句柄”拿到：
                // 主动请求期间必须把监听的读让开，否则监听会把应答抢走，请求方只能超时。
                if (_scanActive || DateTime.UtcNow.Ticks < LinkPauseUntilTicks)
                {
                    Thread.Sleep(40);
                    continue;
                }

                List<byte[]> frames = HidNative.ReadInputReports(iface, 150, 4);
                if (frames.Count == 0) continue;

                DateTime now = DateTime.UtcNow;
                lock (_linkSync)
                {
                    _linkLastTrafficUtc = now;
                    for (int i = 0; i < frames.Count; i++)
                    {
                        byte[] f = frames[i];
                        if (f.Length < 6) continue;
                        if (f[1] != 0x01) continue;
                        if (featureIndex <= 0 || f[2] != featureIndex || f[3] != 0x02) continue;
                        int mv = (f[4] << 8) + f[5];
                        if (mv < 1000 || mv > 6000) continue;
                        _linkPassiveMv = mv;
                        _linkPassiveUtc = now;
                    }
                }
            }
        }

        public BatteryService(AppSettings settings)
        {
            Settings = settings ?? new AppSettings();
            _router = new ProtocolRouter(Settings);
        }

        /// <summary>
        /// 系统报告设备到达/移除时调用：清空型号级探测缓存，下一次刷新重新探测。
        /// 可能在界面线程调用。
        /// </summary>
        public void InvalidateProbes()
        {
            try { _router.InvalidateAll(); }
            catch { }
        }

        private void WriteLog(string message)
        {
            Action<string> handler = Log;
            if (handler != null)
            {
                try { handler(message); }
                catch { }
            }
        }

        public ScanResult Scan()
        {
            _scanActive = true;
            ScanResult result = new ScanResult();
            _recheckMs = 0;
            _router.BeginScan();
            List<HidInterfaceInfo> all = HidNative.Enumerate();
            result.Interfaces = all;
            WriteLog(string.Format("枚举到 {0} 个 HID 接口", all.Count));
            List<BtBatteryInfo> bt = BluetoothBattery.Scan();

            Dictionary<string, HidGroup> groups = new Dictionary<string, HidGroup>();
            for (int i = 0; i < all.Count; i++)
            {
                HidInterfaceInfo info = all[i];
                if (info.Vid == 0x3837 && info.Pid == 0x1012) continue; // MCHOSE MagDock
                // 分组键只用 VID/PID：同一型号的不同个体、不同端口合并成同一台设备，
                // 不再把产品名字符串拼进来（换电脑/换驱动导致名字变化会把同型号拆成两张卡）。
                string key = string.Format("{0:X4}:{1:X4}", info.Vid, info.Pid);
                HidGroup g;
                if (!groups.TryGetValue(key, out g))
                {
                    g = new HidGroup();
                    g.Vid = info.Vid;
                    g.Pid = info.Pid;
                    g.Product = info.Product;
                    g.Manufacturer = info.Manufacturer;
                    groups[key] = g;
                }
                g.Interfaces.Add(info);
            }

            List<HidGroup> groupList = new List<HidGroup>(groups.Values);
            for (int i = 0; i < groupList.Count; i++) groupList[i].Kind = DetectKind(groupList[i]);
            groupList.Sort(delegate (HidGroup a, HidGroup b)
            {
                int ka = KindOrder(a.Kind);
                int kb = KindOrder(b.Kind);
                if (ka != kb) return ka.CompareTo(kb);
                return string.Compare(a.Product, b.Product, StringComparison.OrdinalIgnoreCase);
            });

            for (int i = 0; i < groupList.Count; i++)
            {
                HidGroup g = groupList[i];
                if (g.Kind == DeviceKind.Unknown) continue;
                _router.NotePresent(ProtocolRouter.ModelKeyOf(g));
                if (g.Kind == DeviceKind.Logitech && !HasHidppCandidate(g)) continue;

                // 勾选门控（只在入口）：未勾选的型号不读电量、不查状态、不开句柄、不跑阶梯、不探测，也不出卡片。
                // 枚举本身仍会把它记成“已发现”，设置页据此列出供勾选。
                string gateKey = DeviceKeyFor(g);
                if (Settings.NoteDiscovered(gateKey)) _discoveredChanged = true;
                Settings.NoteSeen(gateKey);
                if (!Settings.IsDeviceEnabled(gateKey)) continue;

                // 罗技接收器：开一个只读监听线程，靠它主动推来的帧判断链路是否活着（G HUB 的做法）。
                // 监听线程不在这里启动：它会一直挂着读输入报告，和本轮探测抢同一个应答。
            // 等本轮扫描全部结束、路由也定下来之后再启动（见 Scan 末尾）。

                // 已勾选但上次是“未连接”：只查状态。罗技用一次轻量 ping 判断是否醒着，醒着才在同一轮继续读电量；
                // MCHOSE 只能靠试读判连接（与读数同价，读到即采信）；K02BS 走音频端点 + 蓝牙列表，本来就不开 HID。
                bool lastConnected = Settings.GetRuntime(gateKey).Connected;
                if (!lastConnected && g.Kind == DeviceKind.Logitech && !LogitechPing(g))
                {
                    DeviceCardModel offline = BaseCard(g);
                    offline.CanGpwSetup = true;
                    ApplyMeasured(offline, BatteryResult.Fail("罗技 HID++ 2.0", "设备未响应（轻量 ping 未应答），本轮不读电量", ""), "2.4G", "罗技 HID++ 2.0");
                    result.Cards.Add(offline);
                    WriteLog("罗技 " + gateKey + " 未连接：轻量 ping 未应答，跳过电量读取");
                    continue;
                }
                try
                {
                    DeviceCardModel card = ReadGroup(g, bt);
                    if (card != null)
                    {
                        if (g.Kind == DeviceKind.Logitech) card.CanGpwSetup = true;
                        // 只要这台设备对我们“可见”，卡片就不能凭空消失：负缓存只决定还发不发包，
                        // 不决定显不显示。仍隐藏的情形只有：明确没有电池特性、没有历史电量、
                        // 没有自定义名，且不是我们支持的 GPW 型号（避免凭空多出键盘卡片）。
                        bool hide = card.RouteUnsupported
                            && !Settings.GetLastLevel(card.DeviceKey).HasValue
                            && string.IsNullOrEmpty(Settings.GetDeviceName(card.DeviceKey))
                            && !IsSupportedGpwModel(g);
                        if (!hide)
                        {
                            NoteRouteDecided(card, g);
                            result.Cards.Add(card);
                        }
                    }
                }
                catch (Exception ex)
                {
                    DeviceCardModel card = BaseCard(g);
                    card.LevelText = "—";
                    card.StatusText = "读取失败";
                    card.Detail = ex.Message;
                    card.IsError = true;
                    card.Accent = Color.FromArgb(240, 68, 56);
                    result.Cards.Add(card);
                    WriteLog("读取 " + g.Product + " 失败: " + ex.Message);
                }
            }

            HashSet<string> seenBt = new HashSet<string>();
            for (int i = 0; i < bt.Count; i++)
            {
                BtBatteryInfo b = bt[i];
                if (!b.Level.HasValue) continue;
                string title = CleanBtName(b.Name);
                if (string.IsNullOrEmpty(title)) continue;
                if (title.IndexOf("K02BS", StringComparison.OrdinalIgnoreCase) >= 0) continue; // 已在 K02BS 卡片中处理
                string key = title.ToLowerInvariant();
                if (!seenBt.Add(key)) continue;
                // 型号级键：用设备名（同型号在不同电脑一致），不再用 BTHENUM 实例 ID（每台机器、每次配对都不同）。
                string btKey = "BT:" + title.Trim().ToUpperInvariant();
                Settings.MigrateKey("BT:" + b.InstanceId, btKey);
                if (Settings.NoteDiscovered(btKey)) _discoveredChanged = true;
                Settings.NoteSeen(btKey);
                if (!Settings.IsDeviceEnabled(btKey)) continue;
                DeviceCardModel card = new DeviceCardModel();
                card.Title = title;
                string customBt = Settings.GetDeviceName(btKey);
                if (customBt.Length > 0) card.Title = customBt;
                card.Subtitle = "蓝牙设备";
                card.DeviceKey = btKey;
                card.Mode = "蓝牙";
                card.Connected = true;
                card.HasLevel = true;
                card.Charging = false;
                card.ThemeColor = IsHeadsetName(title) ? Color.FromArgb(46, 144, 250) : Color.FromArgb(79, 70, 229);

                EstimateConfig cfg = Settings.GetEstimateConfig(card.DeviceKey);
                ApplyBtEstimate(card, b.Level.Value, cfg, "蓝牙", "Windows 蓝牙 HFP 电量属性");
                result.Cards.Add(card);
                WriteLog("蓝牙电量: " + b.Name + " = " + b.Level.Value + "% (显示 " + card.LevelText + ")");
            }

            for (int k = 0; k < AppSettings.KnownDeviceKeys.Length; k++)
            {
                string key = AppSettings.KnownDeviceKeys[k];
                int existing = -1;
                for (int i = 0; i < result.Cards.Count; i++)
                {
                    if (string.Equals(result.Cards[i].DeviceKey, key, StringComparison.OrdinalIgnoreCase))
                    {
                        existing = i;
                        break;
                    }
                }
                if (!Settings.IsDeviceEnabled(key))
                {
                    if (existing >= 0) result.Cards.RemoveAt(existing);
                    continue;
                }
                if (existing < 0) result.Cards.Add(Settings.CreatePlaceholderCard(key));
            }

            // 只有“第一次发现新型号”时才落盘一次，正常刷新不写 settings.ini。
            if (_discoveredChanged)
            {
                _discoveredChanged = false;
                try { Settings.Save(); }
                catch { }
            }

            for (int i = 0; i < result.Cards.Count; i++)
            {
                if (result.Cards[i].LinkPending)
                {
                    result.PendingDisconnect = true;
                    break;
                }
            }

            result.RecheckAfterMs = _recheckMs;
            _router.EndScan();

            // 本轮所有读取（含探测）都结束了，现在才启动长连接监听线程：
            // 它一旦开始就常驻读输入报告，会和探测抢同一个应答。
            if (_linkMonitorPending)
            {
                _linkMonitorPending = false;
                if (_linkGroup != null && _linkFeatureIndex > 0) EnsureLinkMonitor(_linkGroup);
            }

            // GPW 的观测最高电压/自动下调：攒到有实际变化时再落盘，避免每轮刷新都写 ini。
            if (_gpwPersistPending)
            {
                _gpwPersistPending = false;
                Settings.SaveGpwRuntime();
            }

            if (result.Cards.Count == 0)
            {
                WriteLog("没有发现可读取电量的设备");
            }
            _scanActive = false;
            return result;
        }

        private static int KindOrder(DeviceKind kind)
        {
            switch (kind)
            {
                case DeviceKind.Mchose: return 0;
                case DeviceKind.K02BS: return 1;
                case DeviceKind.Jieli: return 2;
                case DeviceKind.Logitech: return 3;
                case DeviceKind.Generic: return 4;
                default: return 9;
            }
        }

        private DeviceKind DetectKind(HidGroup g)
        {
            if (g.Vid == 0x3837) return DeviceKind.Mchose;
            if (g.Vid == 0x10D6 && g.Pid == 0x4801) return DeviceKind.K02BS;
            if ((g.Vid == 0x3654 || g.Vid == 0x4C4A) && HasUsagePage(g, 0xFF02)) return DeviceKind.Jieli;
            if (g.Vid == 0x046D) return DeviceKind.Logitech;
            if (HasBatteryCap(g)) return DeviceKind.Generic;
            return DeviceKind.Unknown;
        }

        private static bool HasUsagePage(HidGroup g, ushort page)
        {
            for (int i = 0; i < g.Interfaces.Count; i++)
                if (g.Interfaces[i].UsagePage == page) return true;
            return false;
        }

        private bool HasBatteryCap(HidGroup g)
        {
            for (int i = 0; i < g.Interfaces.Count; i++)
            {
                List<ValueCap> caps = HidNative.GetValueCaps(g.Interfaces[i]);
                for (int k = 0; k < caps.Count; k++)
                    if (caps[k].IsBatteryUsage) return true;
            }
            return false;
        }

        private static bool HasHidppCandidate(HidGroup g)
        {
            for (int i = 0; i < g.Interfaces.Count; i++)
            {
                if (g.Interfaces[i].OutputLen >= 7) return true;
            }
            return false;
        }

        /// <summary>
        /// GPW 的三个型号级 PID：接收器 046D:C539、本体 046D:4079、有线 046D:C088。
        /// 这三个 PID 来自任务段给定的公开型号标识；这几个型号即使一时读不到电量也保卡，
        /// 只在状态行说明，避免卡片整张消失让用户以为设备被丢了。
        /// </summary>
        private static bool IsSupportedGpwModel(HidGroup g)
        {
            if (g == null || g.Vid != 0x046D) return false;
            return g.Pid == 0xC539 || g.Pid == 0x4079 || g.Pid == 0xC088;
        }

        /// <summary>卡片键 = 勾选/配置用的型号级键（不看实例路径、端口、序列号）。</summary>
        private static string DeviceKeyFor(HidGroup g)
        {
            if (g.Vid == 0x10D6 && g.Pid == 0x4801) return "K02BS";
            if (g.Vid == 0x3837) return "MCHOSE";
            return g.Vid.ToString("X4") + ":" + g.Pid.ToString("X4");
        }

        /// <summary>
        /// 罗技“只查状态”：对缓存里的设备索引问一次特性索引（不读电量值）。
        /// 复用现有 GetFeatureIndex（400ms 超时），不新增任何特征号。
        /// </summary>
        private bool LogitechPing(HidGroup g)
        {
            int cached = _router.GetCachedDeviceIndex(ProtocolRouter.ModelKeyOf(g));
            if (cached < 0) cached = LogitechReader.DirectDeviceIndex;
            return LogitechReader.Ping(g, cached, WriteLog);
        }

        /// <summary>
        /// 首次成功读到时补一条型号级路由记录，让缓存表覆盖全部单变体协议。
        /// 命中变体的读取路径自己已写过记录（EnsureRecord 遇到已有记录直接返回）。
        /// </summary>
        private void NoteRouteDecided(DeviceCardModel card, HidGroup g)
        {
            if (card == null || !card.HasLevel || card.IsEstimated) return;
            ProtocolKind kind = ProtocolKind.Unknown;
            switch (g.Kind)
            {
                case DeviceKind.Mchose: kind = ProtocolKind.MchoseFeature; break;
                case DeviceKind.K02BS: kind = ProtocolKind.HidActions; break;
                case DeviceKind.Jieli: kind = ProtocolKind.Jieli; break;
                case DeviceKind.Logitech: return; // 罗技有百分比/电压两条路，路由只能由探测写入
                case DeviceKind.Generic: kind = ProtocolKind.GenericHid; break;
            }
            if (kind == ProtocolKind.Unknown) return;
            _router.EnsureRecord(ProtocolRouter.ModelKeyOf(g), kind, ProbeValueKind.Percentage, ProtocolRouter.FirmwareHint(g));
        }

        private static bool HasVendorInterface(HidGroup g, ushort vid)
        {
            return g.Vid == vid;
        }

        private DeviceCardModel BaseCard(HidGroup g)
        {
            DeviceCardModel card = new DeviceCardModel();
            card.Group = g;
            card.DeviceKey = DeviceKeyFor(g);
            if (g.Kind == DeviceKind.Mchose)
            {
                card.Title = string.IsNullOrEmpty(g.Product) ? "MCHOSE 无线鼠标" : g.Product;
                card.Subtitle = string.Format("VID {0:X4} · PID {1:X4} · 迈从 2.4G 鼠标", g.Vid, g.Pid);
                card.ThemeColor = Color.FromArgb(18, 183, 106);
            }
            else if (g.Kind == DeviceKind.K02BS)
            {
                card.Title = "西伯利亚 K02BS";
                card.Subtitle = string.Format("VID {0:X4} · PID {1:X4} · 2.4G / 蓝牙 游戏耳机", g.Vid, g.Pid);
                card.ThemeColor = Color.FromArgb(46, 144, 250);
            }
            else if (g.Kind == DeviceKind.Jieli)
            {
                card.Title = string.IsNullOrEmpty(g.Product) ? "杰理方案无线耳机" : g.Product;
                card.Subtitle = string.Format("VID {0:X4} · PID {1:X4} · 2.4G 耳机接收器", g.Vid, g.Pid);
                card.ThemeColor = Color.FromArgb(46, 144, 250);
            }
            else if (g.Kind == DeviceKind.Logitech)
            {
                card.Title = string.IsNullOrEmpty(g.Product) ? "罗技无线设备" : g.Product;
                card.Subtitle = string.Format("VID {0:X4} · PID {1:X4} · Logitech HID++", g.Vid, g.Pid);
                card.ThemeColor = IsHeadsetName(g.Product) ? Color.FromArgb(46, 144, 250) : Color.FromArgb(18, 183, 106);
            }
            else
            {
                card.Title = string.IsNullOrEmpty(g.Product) ? "HID 设备" : g.Product;
                card.Subtitle = string.Format("VID {0:X4} · PID {1:X4} · 标准 HID 电量", g.Vid, g.Pid);
                card.ThemeColor = IsHeadsetName(g.Product) ? Color.FromArgb(46, 144, 250) : Color.FromArgb(18, 183, 106);
            }
            // 自定义显示名优先；没改过名就保持上面识别出的默认名。
            string customTitle = Settings.GetDeviceName(card.DeviceKey);
            if (customTitle.Length > 0) card.Title = customTitle;
            return card;
        }

        private static bool IsHeadsetName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string s = name.ToLowerInvariant();
            return s.Contains("耳机") || s.Contains("headset") || s.Contains("headphone") || s.Contains("earphone")
                || s.Contains("k02") || s.Contains("k03") || s.Contains("xiberia") || s.Contains("西伯利亚")
                || s.Contains("jieli") || s.Contains("杰理") || s.Contains("hands-free");
        }

        private static BtBatteryInfo FindBt(List<BtBatteryInfo> bt, string namePart)
        {
            BtBatteryInfo fallback = null;
            for (int i = 0; i < bt.Count; i++)
            {
                if (string.IsNullOrEmpty(bt[i].Name) || bt[i].Name.IndexOf(namePart, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (bt[i].HasProperty || bt[i].Level.HasValue) return bt[i];
                if (fallback == null) fallback = bt[i];
            }
            return fallback;
        }

        private DeviceCardModel ReadGroup(HidGroup g, List<BtBatteryInfo> bt)
        {
            DeviceCardModel card = BaseCard(g);
            if (g.Kind == DeviceKind.K02BS)
                return ReadK02BS(g, card, bt);

            BatteryResult r = null;
            string mode = "2.4G";
            string detail = "";
            switch (g.Kind)
            {
                case DeviceKind.Mchose:
                    r = ReadMchoseRouted(g);
                    detail = "MCHOSE 私有协议 · Feature 0x11 / 0x06";
                    break;
                case DeviceKind.Jieli:
                    r = JieliReader.Read(g, WriteLog);
                    detail = "杰理 JL_RCSP · TLV 0x01";
                    break;
                case DeviceKind.Logitech:
                    r = ReadLogitechRouted(g, card);
                    mode = "2.4G";
                    detail = "罗技 HID++ 2.0";
                    break;
                case DeviceKind.Generic:
                    r = GenericBatteryReader.Read(g, WriteLog);
                    mode = "HID";
                    detail = "标准 HID Battery Strength";
                    break;
            }
            if (r == null) r = BatteryResult.Fail("未知", "没有可用的读取方式", "");
            ApplyMeasured(card, r, mode, detail);
            return card;
        }

        /// <summary>
        /// MCHOSE 路由：命中缓存就只跑命中的那个变体（Feature / V2），未命中才跑完整探测链。
        /// 判定只看 VID/PID；超时不做负缓存，保留原有的快速重试。
        /// </summary>
        private BatteryResult ReadMchoseRouted(HidGroup g)
        {
            string modelKey = ProtocolRouter.ModelKeyOf(g);
            int firmware = ProtocolRouter.FirmwareHint(g);
            ProbeRecord rec = _router.Find(modelKey);

            if (_router.IsBlocked(modelKey, firmware))
                return BatteryResult.NotSupported("MCHOSE 私有协议", "该型号已探测为不支持（缓存有效期内不再探测）", "");

            if (_router.IsUsable(rec, firmware))
            {
                BatteryResult cached = MchoseReader.ReadVariant(g, rec.Protocol, WriteLog);
                if (cached != null && cached.Ok) { _router.NoteHealthy(modelKey); return cached; }
                _router.NoteTimeout(modelKey);
                if (!_router.CanProbe(modelKey))
                    return cached != null ? cached : BatteryResult.Fail("MCHOSE 私有协议", "读取失败", "");
            }
            else if (rec != null && !_router.CanProbe(modelKey))
            {
                return BatteryResult.Fail("MCHOSE 私有协议", "探测间隔未到，稍后重试", "");
            }

            _router.NoteProbeAttempt(modelKey);
            ProtocolKind used;
            BatteryResult r = MchoseReader.ReadProbe(g, WriteLog, out used);
            if (r != null && r.Ok) _router.RecordSuccess(modelKey, used, ProbeValueKind.Percentage, 0, firmware, -1);
            else if (r != null && r.Unsupported) _router.RecordUnsupported(modelKey, firmware);
            else _router.NoteTimeout(modelKey);
            return r;
        }

        /// <summary>取（或建）某台设备的 GPW 滤波状态；只存内存。</summary>
        private GpwFilterState GetGpwState(string key)
        {
            GpwFilterState st;
            if (!_gpwStates.TryGetValue(key, out st))
            {
                st = new GpwFilterState();
                _gpwStates[key] = st;
            }
            return st;
        }

        /// <summary>
        /// 罗技路由：按 VID=0x046D + 接口长度选路；缓存记录命中类型（百分比/电压）与设备索引。
        /// 设备索引只存内存，不落盘（它是本次连接里的寻址方式，不是身份）。
        /// </summary>
        private BatteryResult ReadLogitechRouted(HidGroup g, DeviceCardModel card)
        {
            string modelKey = ProtocolRouter.ModelKeyOf(g);
            int firmware = ProtocolRouter.FirmwareHint(g);
            ProbeRecord rec = _router.Find(modelKey);

            // 设备自己推来的电压（0x1001 通知）优先：命中时本轮一个请求都不发。
            int passiveMv;
            if (TryGetPassiveVoltage(out passiveMv))
            {
                BatteryResult pushed = BatteryResult.Fail("罗技 HID++ 2.0（设备推送）", "设备主动上报电压，本轮未发请求", "");
                pushed.HasVoltage = true;
                pushed.VoltageMv = passiveMv;
                _router.NoteHealthy(modelKey);
                return pushed;
            }

            if (_router.IsBlocked(modelKey, firmware))
            {
                card.RouteUnsupported = true;
                return BatteryResult.NotSupported("罗技 HID++ 2.0", "该型号已探测为不支持（缓存有效期内不再探测）", "");
            }

            if (_router.IsUsable(rec, firmware))
            {
                // 设备槽位优先用探测记录里持久化的那个：只存在内存里的槽位重启后就没有了，
                // 退回 0xFF（接收器自身）只会读到“有人应答但没有电量”，两轮之后就翻成未连接。
                int deviceIndex = rec.DeviceIndex > 0 ? rec.DeviceIndex : _router.GetCachedDeviceIndex(modelKey);
                if (deviceIndex < 0) deviceIndex = LogitechReader.FindPresentDevice(g, WriteLog);
                if (deviceIndex < 0)
                {
                    deviceIndex = LogitechReader.DirectDeviceIndex;
                }
                else
                {
                    _router.SetCachedDeviceIndex(modelKey, deviceIndex);
                }
                // 特性号必须来自探测记录：IsUsable 已保证 FeatureId > 0。
                int featureId = rec.FeatureId;
                LogitechReader.Outcome cached = LogitechReader.ReadFeature(g, deviceIndex, featureId, WriteLog);
                if (cached != null && cached.Result != null && (cached.Result.Ok || cached.Result.HasVoltage))
                {
                    _router.NoteHealthy(modelKey);
                    if (cached.Result.HasVoltage)
                    {
                        lock (_linkSync) { _linkGroup = g; }
                        _linkMonitorPending = true;
                    }
                    _router.RememberDeviceIndex(modelKey, deviceIndex);
                    return cached.Result;
                }
                _router.NoteTimeout(modelKey);
                if (!_router.CanProbe(modelKey))
                    return cached != null ? cached.Result : BatteryResult.Fail("罗技 HID++ 2.0", "读取失败", "");
            }
            else if (!_router.CanProbe(modelKey))
            {
                return BatteryResult.Fail("罗技 HID++ 2.0", "探测间隔未到，稍后重试", "");
            }
            else if (_router.ProbeCount(modelKey) >= ProtocolRouter.MaxProbesPerSession)
            {
                // 一次会话内只探测有限次数：接收器没配对、鼠标没插上时不发包，也不再拖慢刷新。
                return BatteryResult.Fail("罗技 HID++ 2.0", "探测次数已达上限，等待设备重新接入", "");
            }

            _router.NoteProbeAttempt(modelKey);
            LogitechReader.Outcome outcome = LogitechReader.Probe(g, WriteLog);
            if (outcome == null || outcome.Result == null)
                return BatteryResult.Fail("罗技 HID++ 2.0", "没有可用的 HID++ 接口", "");

            if (outcome.Result.Ok || outcome.Result.HasVoltage)
            {
                _router.SetCachedDeviceIndex(modelKey, outcome.DeviceIndex);
                _router.ResetNoBatteryStrike(modelKey);
                _router.RecordSuccess(modelKey, outcome.Protocol, outcome.Kind, outcome.FeatureId, firmware, outcome.DeviceIndex);
                if (outcome.Protocol == ProtocolKind.LogitechVoltage)
                {
                    // 记住电压特征索引，监听线程据此认出设备主动推来的电压通知。
                    lock (_linkSync) { _linkFeatureIndex = outcome.FeatureId; _linkGroup = g; }
                    _linkMonitorPending = true;
                }
                return outcome.Result;
            }
            if (outcome.Answered)
            {
                // 设备明确回答“没有这些电池特性”。要连续两轮都这样才写负缓存：
                // 冷启动/有线切换的那几秒里，接收器会正常应答但特性表还没准备好。
                card.RouteUnsupported = true;
                int strikes = _router.BumpNoBatteryStrike(modelKey);
                if (strikes >= 2)
                {
                    _router.RecordUnsupported(modelKey, firmware);
                    WriteLog("罗技 " + modelKey + " 连续 " + strikes + " 轮确认没有电池特性，写入负缓存");
                }
            }
            else
            {
                _router.NoteTimeout(modelKey);
            }
            return outcome.Result;
        }

        private void ApplyMeasured(DeviceCardModel card, BatteryResult r, string mode, string detail)
        {
            card.Source = r.Source;
            card.Raw = r.Raw;
            card.Mode = mode;
            if (r != null && r.HasVoltage && !r.Level.HasValue)
            {
                // 电压特征（0x1001）：换算成百分比，再走“已连接 + 有电量”的统一显示。
                // 外部供电有效时跳过映射，主数字直接写“充电中”，避免插线瞬间数字跳变。
                bool pendingVolt = false;
                if (!string.IsNullOrEmpty(card.DeviceKey))
                    Settings.UpdateLinkState(card.DeviceKey, true, out pendingVolt);
                card.Connected = true;
                card.LinkPending = false;
                card.CanGpwSetup = true;

                GpwConfig cfg = Settings.GetGpwConfig();
                string stateKey = string.IsNullOrEmpty(card.DeviceKey) ? "GPW" : card.DeviceKey;
                GpwFilterState st = GetGpwState(stateKey);
                bool charging;
                bool anomaly;
                double smoothed;
                int level = GpwBattery.Feed(cfg, st, r.VoltageMv, r.Charging == true, DateTime.UtcNow, out charging, out anomaly, out smoothed);

                double beforeMax = cfg.ObservedMaxMv;
                int beforeSamples = cfg.Samples;
                GpwBattery.Observe(cfg, st, r.VoltageMv, r.Charging == true, DateTime.UtcNow);
                bool autoAdjusted = GpwBattery.TryAutoAdjust(cfg);
                if (autoAdjusted) WriteLog(string.Format("GPW 满电锚点自动下调到 {0:0} mV（观测最高 {1:0} mV）", cfg.FullMv, cfg.ObservedMaxMv));
                // 只在观测值真的变化、样本凑整、或发生自动下调时落盘，避免每次刷新都写 ini。
                if (cfg.ObservedMaxMv > beforeMax || autoAdjusted || (cfg.Samples > beforeSamples && cfg.Samples % 10 == 0))
                    _gpwPersistPending = true;

                card.Charging = charging;
                if (charging)
                {
                    bool had = st != null && st.Displayed >= 0;
                    card.HasLevel = had;
                    card.Level = had ? (int?)st.Displayed : null;
                    card.LevelText = "充电中";
                    card.StatusText = "充电中";
                    card.StatusLine = "HID++ 电压特征 · 外部供电，暂停换算";
                    card.EstimateHint = string.Format("充电中 · 电压 {0:0} mV", r.VoltageMv);
                    card.Accent = card.ThemeColor;
                }
                else if (anomaly)
                {
                    card.HasLevel = true;
                    card.Level = 0;
                    card.LevelText = "0%";
                    card.IsError = true;
                    card.StatusText = "电压异常";
                    card.StatusLine = string.Format("HID++ 电压特征 · {0:0} mV 低于 3000 mV 下限", r.VoltageMv);
                    card.EstimateHint = "读数低于绝对下限，按 0% 显示";
                    card.Accent = Color.FromArgb(247, 144, 9);
                }
                else
                {
                    int shown = level < 0 ? 0 : level;
                    card.HasLevel = true;
                    card.Level = shown;
                    card.LevelText = shown + "%";
                    card.StatusText = "电压估算";
                    card.StatusLine = string.Format("HID++ 电压特征 · {0:0} mV · 平滑 {1:0} mV", r.VoltageMv, smoothed);
                    card.Accent = shown < 30 ? Color.FromArgb(240, 68, 56) : card.ThemeColor;
                    int suggest = GpwBattery.SuggestFullMv(cfg);
                    if (suggest > 0)
                        card.EstimateHint = string.Format("观测最高 {0:0} mV · 可把满电锚点调到 {1} mV", cfg.ObservedMaxMv, suggest);
                    else
                        card.EstimateHint = string.Format("观测最高 {0:0} mV · 样本 {1}", cfg.ObservedMaxMv, cfg.Samples);
                    if (!string.IsNullOrEmpty(card.DeviceKey)) Settings.SetLastLevel(card.DeviceKey, shown);
                }
                card.Detail = detail + (string.IsNullOrEmpty(r.Detail) ? "" : " · " + r.Detail);
                return;
            }
            if (r != null && r.Ok && r.Level.HasValue)
            {
                if (!string.IsNullOrEmpty(card.DeviceKey))
                {
                    bool pending;
                    Settings.UpdateLinkState(card.DeviceKey, true, out pending);
                }
                card.Connected = true;
                card.LinkPending = false;
                card.HasLevel = true;
                card.Level = r.Level;
                card.LevelText = r.Level.Value + "%";
                card.Charging = r.Charging == true;
                card.StatusText = r.Charging == true ? "充电中" : (r.Charging == false ? "放电中" : "已读取");
                card.StatusLine = mode + " 实测 · 刚刚";
                card.Detail = detail;
                card.Accent = r.Level.Value < 30 ? Color.FromArgb(240, 68, 56) : card.ThemeColor;
                if (!string.IsNullOrEmpty(card.DeviceKey)) Settings.SetLastLevel(card.DeviceKey, r.Level.Value);
            }
            else
            {
                int? last = string.IsNullOrEmpty(card.DeviceKey) ? null : Settings.GetLastLevel(card.DeviceKey);
                bool effective = false;
                bool pending = false;
                if (!string.IsNullOrEmpty(card.DeviceKey))
                    effective = Settings.UpdateLinkState(card.DeviceKey, false, out pending);

                // 罗技：接收器还在枚举里，就说明链路没被拔掉；鼠标休眠/关机只是没有数据。
                // 用被动监听得到的“最近活动”来判断是刚睡下还是彻底没动静，永远不标红色“未连接”。
                bool logitech = detail == "罗技 HID++ 2.0";
                if (logitech)
                {
                    effective = true;
                    pending = false;
                }
                card.Connected = effective;
                card.LinkPending = pending;

                if (last.HasValue)
                {
                    card.HasLevel = true;
                    card.Level = last;
                    card.LevelText = last.Value + "%";
                    card.StatusText = logitech ? "休眠" : (effective ? "连接波动" : "未连接");
                    card.StatusLine = logitech ? LinkIdleText() : (effective ? "读取超时 · 10秒内自动重试" : "未连接 · 最后电量");
                    card.EstimateHint = "最后电量";
                    card.Detail = detail + (logitech ? " · 接收器在线，设备暂无数据" : " · 设备暂时无响应，显示最后读数");
                    card.IsError = false;
                    card.Accent = last.Value < 30 ? Color.FromArgb(240, 68, 56) : card.ThemeColor;
                }
                else
                {
                    card.HasLevel = false;
                    card.LevelText = "—";
                    card.StatusText = logitech ? "休眠" : (effective ? "读取中" : (r != null && r.Unsupported ? "未提供电量" : "未连接"));
                    card.StatusLine = logitech ? LinkIdleText() : (effective ? "读取超时 · 10秒内自动重试" : mode + " 未连接");
                    card.Detail = detail + (r == null || string.IsNullOrEmpty(r.Detail) ? "" : " · " + r.Detail);
                    card.IsError = !effective && !logitech;
                    card.Accent = effective || logitech ? Color.FromArgb(247, 144, 9) : Color.FromArgb(152, 162, 179);
                }
            }
        }

        /// <summary>罗技“休眠”状态行：说明最后一次收到帧/读数是多久以前。</summary>
        private string LinkIdleText()
        {
            DateTime last = LinkLastActivityUtc;
            if (last == DateTime.MinValue) return "接收器在线 · 尚未收到设备数据";
            double minutes = (DateTime.UtcNow - last).TotalMinutes;
            if (minutes < 1.0) return "接收器在线 · 刚刚有数据";
            return string.Format("接收器在线 · 最后活动 {0:0} 分钟前", minutes);
        }

        /// <summary>
        /// 统一的蓝牙阶梯逻辑：校验上报值是否属于配置阶梯；主数字锁定蓝牙实测档位。
        /// </summary>
        private void ApplyBtEstimate(DeviceCardModel card, int reported, EstimateConfig cfg, string mode, string detail)
        {
            card.CanEstimate = true;
            card.EstimateEnabled = cfg.Enabled;
            card.Mode = mode;
            card.Detail = detail;
            card.Connected = true;
            card.LinkPending = false;
            card.HasLevel = true;
            card.Charging = false;
            card.EstimateInvalid = false;
            card.EstimateWarning = "";

            int level = reported;
            double used = 0;
            int next = 0;
            bool invalid = false;
            string reason = "";
            int projected = reported;

            if (cfg.Enabled)
                level = Settings.EstimateBt(card.DeviceKey, reported, out used, out next, out invalid, out reason, out projected);
            else
                Settings.SetLastLevel(card.DeviceKey, reported);

            card.Level = level;
            card.LevelText = level + "%";
            card.IsEstimated = cfg.Enabled && !invalid;
            card.EstimateInvalid = invalid;
            card.EstimateWarning = reason;

            if (invalid)
            {
                card.StatusText = "设置有误";
                card.StatusLine = string.Format("{0} · 蓝牙阶段 {1}% · 阶梯设置有误", mode, reported);
                card.EstimateHint = reason + "，请点右侧“设置”修正";
                card.Source = "蓝牙实测（阶梯校验失败）";
                card.Accent = level < 30 ? Color.FromArgb(240, 68, 56) : Color.FromArgb(247, 144, 9);
                return;
            }

            card.StatusText = "估算";
            card.StatusLine = string.Format("{0} · 蓝牙反馈 {1}%", mode, reported);

            if (cfg.Enabled)
            {
                card.EstimateHint = string.Format("蓝牙反馈电量{0}% · 已保持{1:0.#}小时", reported, used);
                RuntimeState hintState = Settings.GetRuntime(card.DeviceKey);
                if (mode == "蓝牙" && hintState.HoldFrozen)
                    card.EstimateHint += "（蓝牙模式下已冻结）";
                card.Source = "蓝牙阶梯时间推算";
                card.Accent = level < 30 ? Color.FromArgb(240, 68, 56) : card.ThemeColor;

                if (mode == "双模")
                {
                    RuntimeState st = Settings.GetRuntime(card.DeviceKey);
                    st.HasLastDual = true;
                    st.LastDualLevel = level;
                    st.LastDualReported = reported;
                    st.LastDualHeldHours = used;
                    st.Frozen = false;
                    // 双模下每次刷新都会更新快照：只在内存改，由合并写统一落盘。
                    Settings.SaveDeferred();
                }
            }
            else
            {
                card.EstimateHint = "蓝牙阶梯电量（估算已关闭）";
                card.Source = "Windows 蓝牙";
                card.Accent = level < 30 ? Color.FromArgb(240, 68, 56) : card.ThemeColor;
            }
        }

        private void RequestRecheck(int ms)
        {
            if (ms < 200) ms = 200;
            if (_recheckMs == 0 || ms < _recheckMs) _recheckMs = ms;
        }

        /// <summary>
        /// 重新进入双模后的 3 秒等待期间，先显示上一次双模/最后读数。
        /// </summary>
        private DeviceCardModel ShowK02BSSnapshot(DeviceCardModel card, EstimateConfig cfg, RuntimeState st,
            string mode, bool connected, bool pending)
        {
            int? last = Settings.GetLastLevel("K02BS");
            bool has = false;
            int level = 0;
            int reported = 0;
            double hours = 0;
            if (st.HasLastDual)
            {
                has = true; level = st.LastDualLevel; reported = st.LastDualReported; hours = st.LastDualHeldHours;
            }
            else if (last.HasValue)
            {
                has = true; level = last.Value; reported = last.Value; hours = 0;
            }

            card.CanEstimate = true;
            card.EstimateEnabled = cfg.Enabled;
            card.HasLevel = has;
            card.Level = has ? (int?)level : null;
            card.LevelText = has ? level + "%" : "—";
            card.Charging = false;
            card.IsEstimated = false;
            card.EstimateInvalid = false;
            card.EstimateWarning = "";
            card.Connected = connected;
            card.LinkPending = pending;
            card.Mode = mode;
            card.StatusText = "等待蓝牙";
            card.StatusLine = mode + " · 3秒后更新蓝牙";
            card.EstimateHint = has
                ? string.Format("蓝牙反馈电量{0}% · 已保持{1:0.#}小时", reported, hours)
                : "等待蓝牙反馈";
            if (st.HoldFrozen && has) card.EstimateHint += "（蓝牙模式下已冻结）";
            card.Detail = "模式切换，3秒后读取一次蓝牙电量";
            card.Source = "等待蓝牙反馈";
            card.Accent = has ? (level < 30 ? Color.FromArgb(240, 68, 56) : card.ThemeColor) : Color.FromArgb(152, 162, 179);
            return card;
        }

        private DeviceCardModel ReadK02BS(HidGroup g, DeviceCardModel card, List<BtBatteryInfo> bt)
        {
            BtBatteryInfo b = FindBt(bt, "K02BS");
            bool btConnected = b != null && (b.HasProperty || b.Level.HasValue);
            AudioLinkInfo link = AudioLink.IsK02BSLinked();
            WriteLog("K02BS 2.4G: " + link.Detail + " ; BT=" + (btConnected ? "connected" : "-"));

            EstimateConfig cfg = Settings.GetEstimateConfig("K02BS");
            card.CanEstimate = true;
            card.EstimateEnabled = cfg.Enabled;

            bool physical = link.Linked || btConnected;
            bool pending;
            bool effective = Settings.UpdateLinkState("K02BS", physical, out pending);

            string mode = link.Linked
                ? (btConnected ? "双模" : "2.4G")
                : (btConnected ? "蓝牙" : "未连接");
            RuntimeState st = Settings.GetRuntime("K02BS");
            if (!string.Equals(st.LastMode, mode, StringComparison.Ordinal))
            {
                st.LastMode = mode;
                st.ModeSinceUtc = DateTime.UtcNow;
                st.ModeCheckDone = false;
                Settings.SaveDeferred();
            }
            double modeElapsed = (DateTime.UtcNow - st.ModeSinceUtc).TotalSeconds;
            if (modeElapsed < 0) modeElapsed = 0;

            // 只有重新进入“双模”时才做 3 秒补读；仅蓝牙模式不再补读/不再冻结显示。
            bool trackMode = cfg.Enabled && mode == "双模";
            bool waiting3s = trackMode && !st.ModeCheckDone && modeElapsed < 3.0;
            if (waiting3s)
            {
                RequestRecheck((int)Math.Max(200, (3.0 - modeElapsed) * 1000.0));
                return ShowK02BSSnapshot(card, cfg, st, mode, effective, pending);
            }
            if (trackMode && !st.ModeCheckDone && modeElapsed >= 3.0)
            {
                st.ModeCheckDone = true;
                Settings.SaveDeferred();
            }

            if (cfg.Enabled && mode == "蓝牙")
            {
                if (!st.HoldFrozen)
                {
                    double held = 0;
                    if (st.HasBtStage && st.BtStageUtc != DateTime.MinValue)
                    {
                        held = (DateTime.UtcNow - st.BtStageUtc).TotalHours;
                        if (held < 0) held = 0;
                    }
                    st.HoldFrozen = true;
                    st.FrozenHeldHours = held;
                    Settings.SaveDeferred();
                }
                st.ModeCheckDone = true;
            }

            if (btConnected && b.Level.HasValue && b.Level.Value > 0)
            {
                if (mode == "双模" && st.HoldFrozen)
                {
                    if (st.HasBtStage && st.BtStage == b.Level.Value)
                    {
                        // 双模恢复且阶梯未变：从冻结的保持时间继续，而不是把
                        // 蓝牙模式经过的时间也算进去。
                        st.BtStageUtc = DateTime.UtcNow.AddHours(-Math.Max(0, st.FrozenHeldHours));
                    }
                    st.HoldFrozen = false;
                    Settings.SaveDeferred();
                }
                st.Frozen = false;
                string detail = "Windows 蓝牙 HFP" + (link.Linked ? " · 2.4G 同时连接" : "");
                ApplyBtEstimate(card, b.Level.Value, cfg, mode, detail);
                card.Connected = effective;
                card.LinkPending = pending;
                return card;
            }

            if (link.Linked)
            {
                // 先尝试厂商报文（当前固件没有电量，但保留未来兼容）
                BatteryResult r = ActionsReader.Read(g, WriteLog);
                if (r != null && r.Ok && r.Level.HasValue)
                {
                    ApplyMeasured(card, r, "双模", "Actions 厂商报文");
                    return card;
                }

                // 按需求：2.4G 不做电量估算，只显示最后一次蓝牙读数/等待蓝牙上报
                int? last2 = Settings.GetLastLevel("K02BS");
                card.Mode = "2.4G";
                card.Connected = effective;
                card.LinkPending = pending;
                card.HasLevel = last2.HasValue;
                if (last2.HasValue) { card.Level = last2; card.LevelText = last2.Value + "%"; }
                else card.LevelText = "—";
                card.StatusText = "等待蓝牙";
                card.StatusLine = "2.4G 已连接 · 等待蓝牙电量";
                card.EstimateHint = last2.HasValue ? "最后电量" : "";
                card.Detail = "2.4G 接收器不上报电量；连接蓝牙后按阶梯估算";
                card.Source = "2.4G 音频链路";
                card.Accent = last2.HasValue ? (last2.Value < 30 ? Color.FromArgb(240, 68, 56) : card.ThemeColor) : Color.FromArgb(152, 162, 179);
                return card;
            }

            int? last = Settings.GetLastLevel("K02BS");
            if (btConnected)
            {
                card.Mode = "蓝牙";
                card.Connected = effective;
                card.LinkPending = pending;
                card.HasLevel = last.HasValue;
                if (last.HasValue) { card.Level = last; card.LevelText = last.Value + "%"; }
                else card.LevelText = "—";
                card.StatusText = "蓝牙已连接";
                card.StatusLine = "蓝牙已连接 · 未上报电量";
                card.EstimateHint = last.HasValue ? "最后电量" : "等待蓝牙反馈";
                if (st.HoldFrozen) card.EstimateHint += "（蓝牙模式下已冻结）";
                card.Detail = "耳机已通过蓝牙连接，但 Windows 没有收到 HFP 电量";
                card.Source = "Windows 蓝牙";
                card.Accent = last.HasValue ? (last.Value < 30 ? Color.FromArgb(240, 68, 56) : card.ThemeColor) : Color.FromArgb(152, 162, 179);
                return card;
            }

            if (effective)
            {
                // 10 秒冗余窗口：短暂断连时先保持“已连接”，等重连或超时确认。
                card.Mode = "连接中";
                card.Connected = true;
                card.LinkPending = true;
                card.HasLevel = last.HasValue;
                if (last.HasValue) { card.Level = last; card.LevelText = last.Value + "%"; }
                else card.LevelText = "—";
                card.StatusText = "连接波动";
                card.StatusLine = "连接中断 · 10秒内自动重试";
                card.EstimateHint = last.HasValue ? "最后电量" : "";
                card.Detail = "短暂断连，超过10秒才会正式标记未连接";
                card.Source = "2.4G / 蓝牙链路检测";
                card.Accent = last.HasValue ? (last.Value < 30 ? Color.FromArgb(240, 68, 56) : card.ThemeColor) : Color.FromArgb(247, 144, 9);
                return card;
            }

            card.Mode = "未连接";
            card.Connected = false;
            card.LinkPending = false;
            card.HasLevel = last.HasValue;
            if (last.HasValue) { card.Level = last; card.LevelText = last.Value + "%"; }
            else card.LevelText = "—";
            card.StatusText = "未连接";
            card.StatusLine = last.HasValue ? "未连接 · 最后电量" : "2.4G 音频端点未激活";
            card.EstimateHint = last.HasValue ? "最后电量" : "";
            card.Detail = "耳机可能关机、切到了蓝牙，或接收器未插好";
            card.Source = "2.4G 音频链路检测";
            card.Accent = last.HasValue ? (last.Value < 30 ? Color.FromArgb(240, 68, 56) : card.ThemeColor) : Color.FromArgb(152, 162, 179);
            return card;
        }

        private static string CleanBtName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            string s = name;
            string[] cuts = new string[] {
                " Hands-Free AG Audio", " Hands-Free", " Stereo", " 免提", " 立体声",
                " (Hands-Free)", "（免提）", " (Stereo)", "（立体声）"
            };
            for (int i = 0; i < cuts.Length; i++)
            {
                int p = s.IndexOf(cuts[i], StringComparison.OrdinalIgnoreCase);
                if (p > 0) s = s.Substring(0, p);
            }
            int paren = s.IndexOf(" (");
            if (paren > 0) s = s.Substring(0, paren);
            return s.Trim();
        }

        public static Color ColorForLevel(int level)
        {
            if (level >= 60) return Color.FromArgb(18, 183, 106);
            if (level >= 30) return Color.FromArgb(247, 144, 9);
            return Color.FromArgb(240, 68, 56);
        }

        public BatteryResult ReadInterface(HidInterfaceInfo info)
        {
            WriteLog(string.Format("调试读取: {0} {1} UP=0x{2:X4} {3}", info.Product, info.VidPid, info.UsagePage, info.ReportText));
            HidGroup g = new HidGroup();
            g.Vid = info.Vid; g.Pid = info.Pid; g.Product = info.Product; g.Manufacturer = info.Manufacturer;
            g.Interfaces.Add(info);
            if (info.Vid == 0x3837) { g.Kind = DeviceKind.Mchose; return MchoseReader.Read(g, WriteLog); }
            if (info.Vid == 0x10D6 && info.Pid == 0x4801) { g.Kind = DeviceKind.K02BS; return ActionsReader.Read(g, WriteLog); }
            if (info.Vid == 0x3654 || info.Vid == 0x4C4A) { g.Kind = DeviceKind.Jieli; return JieliReader.Read(g, WriteLog); }
            BatteryResult r = GenericBatteryReader.ReadInterface(info, WriteLog);
            if (r != null && r.Ok) return r;
            BatteryResult logi = LogitechReader.Read(g, WriteLog);
            if (logi != null && logi.Ok) return logi;
            return r != null ? r : BatteryResult.Fail("调试", "该接口没有匹配的读取方式", "");
        }
    }

    internal static class MchoseReader
    {
        /// <summary>完整探测链：Feature 0x11/0x06 优先，失败再退 V2。行为与改前一致。</summary>
        public static BatteryResult Read(HidGroup g, Action<string> log)
        {
            ProtocolKind used;
            return ReadProbe(g, log, out used);
        }

        /// <summary>跑探测链，并报告命中的是哪个变体，供路由层写缓存。</summary>
        public static BatteryResult ReadProbe(HidGroup g, Action<string> log, out ProtocolKind used)
        {
            HidInterfaceInfo iface = FindFeatureInterface(g);
            if (iface == null)
            {
                used = ProtocolKind.Unknown;
                return BatteryResult.Fail("MCHOSE 私有协议", "没有找到 Feature Report 配置接口（0xFF01）", "");
            }
            BatteryResult feature = ReadFeatureWith(g, iface, log);
            if (feature != null && feature.Ok) { used = ProtocolKind.MchoseFeature; return feature; }
            BatteryResult v2 = ReadV2(g, log);
            if (v2 != null && v2.Ok) { used = ProtocolKind.MchoseV2; return v2; }
            used = ProtocolKind.Unknown;
            if (v2 != null && v2.Unsupported) return v2;
            return feature;
        }

        /// <summary>按缓存里的命中变体直读，不再尝试另一个变体。</summary>
        public static BatteryResult ReadVariant(HidGroup g, ProtocolKind kind, Action<string> log)
        {
            if (kind == ProtocolKind.MchoseV2) return ReadV2(g, log);
            HidInterfaceInfo iface = FindFeatureInterface(g);
            if (iface == null)
                return BatteryResult.Fail("MCHOSE 私有协议", "没有找到 Feature Report 配置接口（0xFF01）", "");
            return ReadFeatureWith(g, iface, log);
        }

        public static BatteryResult ReadFeature(HidGroup g, Action<string> log)
        {
            HidInterfaceInfo iface = FindFeatureInterface(g);
            if (iface == null)
                return BatteryResult.Fail("MCHOSE 私有协议", "没有找到 Feature Report 配置接口（0xFF01）", "");
            return ReadFeatureWith(g, iface, log);
        }

        private static HidInterfaceInfo FindFeatureInterface(HidGroup g)
        {
            for (int i = 0; i < g.Interfaces.Count; i++)
            {
                HidInterfaceInfo it = g.Interfaces[i];
                if (it.UsagePage == 0xFF01 && it.FeatureLen >= 21) return it;
            }
            for (int i = 0; i < g.Interfaces.Count; i++)
            {
                if (g.Interfaces[i].FeatureLen >= 21) return g.Interfaces[i];
            }
            return null;
        }

        private static BatteryResult ReadFeatureWith(HidGroup g, HidInterfaceInfo iface, Action<string> log)
        {
            string rawText = "";
            for (int attempt = 0; attempt < 3; attempt++)
            {
                byte[] q = new byte[21];
                for (int i = 0; i < q.Length; i++) q[i] = 0xFF;
                q[0] = 0x11;
                q[1] = (byte)(0x06 ^ 0xFF);
                bool sent = HidNative.SetFeature(iface, q);
                log(string.Format("MCHOSE TX(0x11/0x06) attempt {0}: {1}", attempt + 1, sent ? "OK" : "FAIL"));
                if (!sent) { Thread.Sleep(80); continue; }
                Thread.Sleep(120);
                byte[] r = HidNative.GetFeature(iface, 0x11, 21);
                if (r == null) { log("MCHOSE RX: 无响应"); Thread.Sleep(80); continue; }

                byte[] d = new byte[r.Length - 1];
                for (int i = 1; i < r.Length; i++) d[i - 1] = (byte)(r[i] ^ 0xFF);
                rawText = HidNative.Hex(r);
                log("MCHOSE RX: " + rawText);
                log("MCHOSE 解码: " + HidNative.Hex(d));

                bool hasData = false;
                for (int di = 1; di < d.Length; di++) { if (d[di] != 0) { hasData = true; break; } }
                if (!hasData)
                {
                    log("MCHOSE 收到空应答（鼠标可能休眠）");
                    Thread.Sleep(160);
                    continue;
                }

                int bestOffset = -1;
                int bestLevel = 0;
                int bestCharge = 0;
                int[] offsets = new int[] { 10, 8, 12, 9, 7, 13, 6, 14 };
                for (int oi = 0; oi < offsets.Length; oi++)
                {
                    int off = offsets[oi];
                    if (off + 1 >= d.Length) continue;
                    int b = d[off];
                    int c = d[off + 1];
                    if (b >= 0 && b <= 100 && (c == 0 || c == 1))
                    {
                        if (off == 10) { bestOffset = off; bestLevel = b; bestCharge = c; break; }
                        if (bestOffset < 0) { bestOffset = off; bestLevel = b; bestCharge = c; }
                    }
                }
                if (bestOffset >= 0)
                {
                    if (bestLevel == 0 && attempt < 2)
                    {
                        log("MCHOSE 读到 0%，可能是休眠应答，重试...");
                        Thread.Sleep(180);
                        continue;
                    }
                    int level = bestLevel;
                    // A7 V2 Pro+（PID 100A）直接上报 0-100；老型号可能用 1-10 表示 10%-100%
                    if (level > 0 && level <= 10 && g.Pid != 0x100A) level *= 10;
                    string detail = string.Format("电量字节 @{0} = 0x{1:X2}，充电字节 @{2} = 0x{3:X2}", bestOffset, bestLevel, bestOffset + 1, bestCharge);
                    return BatteryResult.Success(level, bestCharge == 1, "MCHOSE 私有协议（Feature 0x11 / 0x06）", detail, rawText);
                }
                Thread.Sleep(80);
            }

            return BatteryResult.Fail("MCHOSE 私有协议",
                "设备没有返回有效电量。请动一下鼠标唤醒，或关闭 MCHOSE HUB 后重试。原始数据: " + rawText,
                rawText);
        }

        public static BatteryResult ReadV2(HidGroup g, Action<string> log)
        {
            HidInterfaceInfo iface = null;
            for (int i = 0; i < g.Interfaces.Count; i++)
            {
                if (g.Interfaces[i].OutputLen >= 64) { iface = g.Interfaces[i]; break; }
            }
            if (iface == null) return null;

            byte[] f = new byte[64];
            f[0] = 0x4D;
            f[1] = 0x01;
            f[2] = 0x01;
            f[3] = 0x00;
            f[4] = 0x00;
            f[5] = 0x09;
            f[6] = 0x00;
            f[7] = 0x00;
            byte chk = 0;
            for (int i = 2; i <= 7; i++) chk ^= f[i];
            f[8] = chk;

            log("MCHOSE 尝试 V2 协议: " + HidNative.Hex(f, 16));
            bool ok = HidNative.WriteOutput(iface, f, 800);
            if (!ok) { log("MCHOSE V2 写入失败"); return null; }
            List<byte[]> replies = HidNative.ReadInputReports(iface, 700, 8);
            for (int i = 0; i < replies.Count; i++)
            {
                byte[] r = replies[i];
                log("MCHOSE V2 RX: " + HidNative.Hex(r));
                if (r.Length >= 21 && r[0] == 0x4D)
                {
                    int level = r[20];
                    int charge = r[19];
                    if (level >= 0 && level <= 100)
                        return BatteryResult.Success(level, charge == 1, "MCHOSE 协议 V2（0x4D / 0x0900）", "电量字节 @20", HidNative.Hex(r));
                }
            }
            return null;
        }
    }

    internal static class ActionsReader
    {
        public static BatteryResult Read(HidGroup g, Action<string> log)
        {
            HidInterfaceInfo iface = null;
            for (int i = 0; i < g.Interfaces.Count; i++)
            {
                if (g.Interfaces[i].UsagePage == 0xFF90 && g.Interfaces[i].InputLen >= 3) { iface = g.Interfaces[i]; break; }
            }
            if (iface == null)
                return BatteryResult.Fail("Actions 厂商接口", "没有找到 0xFF90 状态接口", "");

            byte[] r = HidNative.GetInputReport(iface, 0x55, iface.InputLen > 0 ? iface.InputLen : 64);
            string raw = r == null ? "" : HidNative.Hex(r);
            log("K02BS 0xFF90/0x55: " + (r == null ? "无响应" : raw));
            if (r == null)
                return BatteryResult.NotSupported("Actions 厂商接口",
                    "接收器没有返回状态报文。请确认耳机已开机并处于 2.4G 模式。", "");

            int b0 = r.Length > 0 ? r[0] : -1;
            int b1 = r.Length > 1 ? r[1] : -1;

            if (b0 >= 0 && b0 <= 100 && (b1 == 0 || b1 == 1))
            {
                return BatteryResult.Success(b0, b1 == 1, "Actions 状态报文（0xFF90 / 0x55）",
                    "电量字节 @0，充电字节 @1", raw);
            }

            if (b0 == 0xAC && b1 == 0xF8)
            {
                return BatteryResult.NotSupported("Actions 状态报文（0xFF90 / 0x55）",
                    "接收器只返回固定状态码 AC F8，未提供电量字段（开关机、充电均不变）。请用耳机语音播报，或切到蓝牙模式。",
                    raw);
            }

            return BatteryResult.NotSupported("Actions 状态报文（0xFF90 / 0x55）",
                "收到了厂商报文，但没有识别出电量字段。原始数据: " + raw, raw);
        }
    }

    internal static class JieliReader
    {
        public static BatteryResult Read(HidGroup g, Action<string> log)
        {
            HidInterfaceInfo iface = null;
            for (int i = 0; i < g.Interfaces.Count; i++)
            {
                HidInterfaceInfo it = g.Interfaces[i];
                if (it.UsagePage == 0xFF02 && it.OutputLen >= 65) { iface = it; break; }
            }
            if (iface == null)
            {
                for (int i = 0; i < g.Interfaces.Count; i++)
                {
                    if (g.Interfaces[i].UsagePage == 0xFF02) { iface = g.Interfaces[i]; break; }
                }
            }
            if (iface == null)
                return BatteryResult.Fail("杰理 JL_RCSP", "没有找到 0xFF02 厂商接口", "");

            byte[] frame = BuildFrame(0, 0x01);
            log("JIELI TX: " + HidNative.Hex(frame, 16));
            bool ok = HidNative.WriteOutput(iface, frame, 800);
            if (!ok) return BatteryResult.Fail("杰理 JL_RCSP", "发送查询失败", "");
            List<byte[]> replies = HidNative.ReadInputReports(iface, 700, 12);
            string lastRaw = "";
            for (int i = 0; i < replies.Count; i++)
            {
                byte[] r = replies[i];
                lastRaw = HidNative.Hex(r);
                log("JIELI RX: " + lastRaw);
                BatteryResult parsed = ParseBattery(r);
                if (parsed != null) return parsed;
            }
            return BatteryResult.Fail("杰理 JL_RCSP", "没有收到电量 TLV（耳机可能未开机/未连接）", lastRaw);
        }

        private static BatteryResult ParseBattery(byte[] r)
        {
            if (r.Length < 14) return null;
            int start = 0;
            if (r[0] == 0x00 && r.Length > 1 && r[1] == 0xFE) start = 0;
            else if (r[0] == 0xFE) start = 1;
            else return null;
            int baseIdx = start;
            if (r.Length <= baseIdx + 9) return null;
            int len = r[baseIdx + 8] | (r[baseIdx + 9] << 8);
            if (len < 0 || baseIdx + 10 + len + 2 > r.Length) return null;
            int p = baseIdx + 10;
            int end = p + len;
            while (p + 2 <= end)
            {
                int type = r[p];
                int l = r[p + 1];
                if (p + 2 + l > end) break;
                if (type == 0x01 && l >= 1)
                {
                    int level = r[p + 2];
                    if (level >= 0 && level <= 100)
                        return BatteryResult.Success(level, null, "杰理 JL_RCSP（TLV 0x01）", "电量 TLV 0x01", HidNative.Hex(r));
                }
                p += 2 + l;
            }
            return null;
        }

        private static ushort Crc16(byte[] data, int start, int len)
        {
            ushort crc = 0xFFFF;
            for (int i = start; i < start + len; i++)
            {
                crc ^= (ushort)(data[i] << 8);
                for (int b = 0; b < 8; b++)
                {
                    if ((crc & 0x8000) != 0) crc = (ushort)((crc << 1) ^ 0x1021);
                    else crc = (ushort)(crc << 1);
                }
            }
            return crc;
        }

        private static byte[] BuildFrame(byte seq, byte tlvType)
        {
            byte[] f = new byte[65];
            f[0] = 0x00;
            f[1] = 0xFE;
            f[2] = seq;
            f[3] = 0x10;
            f[4] = 0x00;
            f[5] = 0x02;
            f[6] = 0x00;
            f[7] = 0x00;
            f[8] = 0x02;
            f[9] = 0x00;
            f[10] = tlvType;
            f[11] = 0x00;
            ushort crc = Crc16(f, 1, 12);
            f[12] = (byte)(crc >> 8);
            f[13] = (byte)(crc & 0xFF);
            return f;
        }
    }

    internal static class GenericBatteryReader
    {
        public static BatteryResult Read(HidGroup g, Action<string> log)
        {
            for (int i = 0; i < g.Interfaces.Count; i++)
            {
                BatteryResult r = ReadInterface(g.Interfaces[i], log);
                if (r != null && r.Ok) return r;
            }
            return BatteryResult.Fail("标准 HID 电量", "没有找到 Battery Strength 字段", "");
        }

        public static BatteryResult ReadInterface(HidInterfaceInfo info, Action<string> log)
        {
            List<ValueCap> caps = HidNative.GetValueCaps(info);
            ValueCap featureCap = null;
            ValueCap inputCap = null;
            for (int i = 0; i < caps.Count; i++)
            {
                if (!caps[i].IsBatteryUsage) continue;
                if (caps[i].ReportType == 2 && featureCap == null) featureCap = caps[i];
                if (caps[i].ReportType == 0 && inputCap == null) inputCap = caps[i];
            }

            if (featureCap != null && info.FeatureLen > 0)
            {
                byte[] rep = HidNative.GetFeature(info, featureCap.ReportId, info.FeatureLen);
                if (rep != null)
                {
                    int? v = HidNative.GetUsageValue(info, featureCap, rep);
                    log(string.Format("标准电量 Feature 0x{0:X2}: {1} -> {2}", featureCap.ReportId, HidNative.Hex(rep), v));
                    if (v.HasValue)
                        return Success(info, featureCap, rep, v.Value);
                }
            }

            if (inputCap != null && info.InputLen > 0)
            {
                List<byte[]> reps = HidNative.ReadInputReports(info, 600, 8);
                for (int i = 0; i < reps.Count; i++)
                {
                    byte[] rep = reps[i];
                    if (rep.Length == 0) continue;
                    int? v = HidNative.GetUsageValue(info, inputCap, rep);
                    if (v.HasValue)
                    {
                        log(string.Format("标准电量 Input 0x{0:X2}: {1} -> {2}", inputCap.ReportId, HidNative.Hex(rep), v));
                        return Success(info, inputCap, rep, v.Value);
                    }
                }
            }

            return BatteryResult.Fail("标准 HID 电量", "接口没有 Battery Strength 字段", "");
        }

        private static BatteryResult Success(HidInterfaceInfo info, ValueCap cap, byte[] rep, int value)
        {
            int level = value;
            int max = cap.LogicalMax;
            if (max > 0 && max != 100 && max != 255) level = (int)Math.Round(value * 100.0 / max);
            else if (max == 255 && value > 100) level = (int)Math.Round(value * 100.0 / 255);
            if (level < 0) level = 0;
            if (level > 100) level = 100;
            return BatteryResult.Success(level, null, "标准 HID 电量字段（" + cap.Name + "）",
                string.Format("UsagePage=0x{0:X4} Usage=0x{1:X4} ReportID=0x{2:X2} 原始值={3} 上限={4}",
                    cap.UsagePage, cap.Usage, cap.ReportId, value, cap.LogicalMax),
                HidNative.Hex(rep));
        }
    }

    internal static class LogitechReader
    {
        /// <summary>HID++ 2.0 中 0xFF 表示接收器自身/直连设备。</summary>
        public const byte DirectDeviceIndex = 0xFF;
        /// <summary>Solaar 公开特性表：0x1000 Battery Status（百分比）。</summary>
        public const int BatteryStatusFeature = 0x1000;
        /// <summary>Solaar 公开特性表：0x1001 Battery Voltage（毫伏）。</summary>
        public const int BatteryVoltageFeature = 0x1001;
        /// <summary>Solaar 公开特性表：0x1004 Unified Battery（百分比）。</summary>
        public const int UnifiedBatteryFeature = 0x1004;
        /// <summary>
        /// 单次探测最多发多少个请求，避免在无电池设备上探测风暴。
        /// 7 个槽位的在场确认 + 长/短两种报文上的特性查询都要算在内。
        /// </summary>
        private const int ProbeBudget = 24;

        public class Outcome
        {
            public BatteryResult Result;
            public ProtocolKind Protocol = ProtocolKind.Unknown;
            public ProbeValueKind Kind = ProbeValueKind.None;
            public int FeatureId;
            public int DeviceIndex = -1;
            /// <summary>是否收到过任何 HID++ 应答：用于区分“设备说没有”和“设备没理我”。</summary>
            public bool Answered;
        }

        /// <summary>完整探测链（调试入口与首次探测共用）。</summary>
        public static BatteryResult Read(HidGroup g, Action<string> log)
        {
            Outcome o = Probe(g, log);
            return o.Result;
        }

        /// <summary>
        /// 首次探测：先用根特征 0x0000 问出“哪些配对槽位真的有设备”，再只在在场槽位上
        /// 找百分比特征（0x1004 → 0x1000），全程无果才退电压特征 0x1001。
        /// 长/短报文各自按报文长度挑接口，避免写到 32 字节的厂商控制口上。
        /// </summary>
        public static Outcome Probe(HidGroup g, Action<string> log)
        {
            Outcome o = new Outcome();
            if (g == null || g.Vid != 0x046D) return o;

            byte[] rids = new byte[] { 0x11, 0x10 };
            int[] devices = new int[] { DirectDeviceIndex, 1, 2, 3, 4, 5, 6 };
            int[] pctFeatures = new int[] { UnifiedBatteryFeature, BatteryStatusFeature };

            int budget = ProbeBudget;
            HidInterfaceInfo presenceIface = null;
            byte presenceRid = 0x11;
            for (int ri = 0; ri < rids.Length && presenceIface == null; ri++)
            {
                HidInterfaceInfo it = PickInterface(g, rids[ri]);
                if (it != null)
                {
                    presenceIface = it;
                    presenceRid = rids[ri];
                }
            }
            if (presenceIface == null) return o;

            // 逐个槽位“先确认在场、再试特性”。只有“根特征正常应答”的槽位才算有设备：
            // 空槽位回错误码，接收器自身（0xFF）也回错误码。
            //
            // 这里按槽位推进而不是先把 7 个槽位扫一遍再统一试特性：空槽位每个要等一个读超时，
            // 先扫完 7 个会把首轮扫描拖到十秒。常见情况（接收器上只挂一只鼠标）只问 0xFF 和
            // 槽位 1 两次就能开始读值；万一槽位 1 上是键盘这种没有电池的设备，再继续往后问。
            int slot = 0;
            while (slot < devices.Length && budget > 0)
            {
                bool presentSlot = false;
                for (; slot < devices.Length; slot++)
                {
                    if (budget <= 0) break;
                    budget--;
                    if (RootPresent(presenceIface, presenceRid, (byte)devices[slot], log))
                    {
                        presentSlot = true;
                        break;
                    }
                }
                if (!presentSlot) break;

                byte deviceIndex = (byte)devices[slot];
                for (int ri = 0; ri < rids.Length; ri++)
                {
                    byte rid = rids[ri];
                    HidInterfaceInfo iface = PickInterface(g, rid);
                    if (iface == null) continue;
                    for (int fi = 0; fi < pctFeatures.Length; fi++)
                    {
                        if (budget <= 0) break;
                        budget--;
                        // Answered 只统计“针对电池特性的查询是否得到应答”。
                        // 在场确认的应答、以及其它无关报文都不能当成“设备说它没有电池”。
                        bool answeredFeature = false;
                        int featureIndex = GetFeatureIndex(iface, rid, deviceIndex, pctFeatures[fi], ref answeredFeature, log);
                        if (answeredFeature) o.Answered = true;
                        if (featureIndex <= 0) continue;
                        BatteryResult r = ReadPercentage(iface, rid, deviceIndex, featureIndex, pctFeatures[fi], log);
                        if (r != null && r.Ok)
                        {
                            o.Result = r;
                            o.Protocol = ProtocolKind.LogitechPercentage;
                            o.Kind = ProbeValueKind.Percentage;
                            o.FeatureId = pctFeatures[fi];
                            o.DeviceIndex = devices[slot];
                            return o;
                        }
                    }
                }

                for (int ri = 0; ri < rids.Length; ri++)
                {
                    byte rid = rids[ri];
                    HidInterfaceInfo iface = PickInterface(g, rid);
                    if (iface == null) continue;
                    if (budget <= 0) break;
                    budget--;
                    bool answeredVolt = false;
                    int featureIndex = GetFeatureIndex(iface, rid, deviceIndex, BatteryVoltageFeature, ref answeredVolt, log);
                    if (answeredVolt) o.Answered = true;
                    if (featureIndex <= 0) continue;
                    BatteryResult v = ReadVoltage(iface, rid, deviceIndex, featureIndex, log);
                    if (v != null && v.HasVoltage)
                    {
                        o.Result = v;
                        o.Protocol = ProtocolKind.LogitechVoltage;
                        o.Kind = ProbeValueKind.Voltage;
                        o.FeatureId = BatteryVoltageFeature;
                        o.DeviceIndex = devices[slot];
                        return o;
                    }
                }

                slot++;
            }

            log("罗技 HID++ 2.0 探测结束：没有找到电池特性");
            o.Result = BatteryResult.Fail("罗技 HID++", "没有找到电池特性", "");
            return o;
        }

        /// <summary>按缓存里的设备索引与特性号直读，不再遍历其它索引/特性。</summary>
        public static Outcome ReadFeature(HidGroup g, int deviceIndex, int featureId, Action<string> log)
        {
            Outcome o = new Outcome();
            if (g == null || g.Vid != 0x046D) return o;
            byte[] rids = new byte[] { 0x11, 0x10 };
            for (int ri = 0; ri < rids.Length; ri++)
            {
                HidInterfaceInfo iface = PickInterface(g, rids[ri]);
                if (iface == null) continue;
                int featureIndex = GetFeatureIndex(iface, rids[ri], (byte)deviceIndex, featureId, ref o.Answered, log);
                if (featureIndex <= 0) continue;
                BatteryResult r;
                if (featureId == BatteryVoltageFeature)
                    r = ReadVoltage(iface, rids[ri], (byte)deviceIndex, featureIndex, log);
                else
                    r = ReadPercentage(iface, rids[ri], (byte)deviceIndex, featureIndex, featureId, log);
                if (r == null) continue;
                if (r.Ok || r.HasVoltage)
                {
                    o.Result = r;
                    o.Protocol = featureId == BatteryVoltageFeature ? ProtocolKind.LogitechVoltage : ProtocolKind.LogitechPercentage;
                    o.Kind = featureId == BatteryVoltageFeature ? ProbeValueKind.Voltage : ProbeValueKind.Percentage;
                    o.FeatureId = featureId;
                    o.DeviceIndex = deviceIndex;
                    return o;
                }
                if (o.Result == null) o.Result = r;
            }
            if (o.Result == null) o.Result = BatteryResult.Fail("罗技 HID++", "没有找到电池特性", "");
            return o;
        }

        /// <summary>
        /// 轻量 ping：只问根特征 0x0000 是否正常应答，不查电池特性、更不读电量值。
        /// 根特征正常应答就认为该槽位上有设备且醒着；否则按未连接处理，
        /// 绝不升级成完整电量读取。
        /// </summary>
        public static bool Ping(HidGroup g, int deviceIndex, Action<string> log)
        {
            if (g == null || g.Vid != 0x046D) return false;
            byte[] rids = new byte[] { 0x11, 0x10 };
            int[] order = deviceIndex >= 0
                ? new int[] { deviceIndex, DirectDeviceIndex, 1, 2, 3, 4, 5, 6 }
                : new int[] { DirectDeviceIndex, 1, 2, 3, 4, 5, 6 };
            for (int ri = 0; ri < rids.Length; ri++)
            {
                HidInterfaceInfo iface = PickInterface(g, rids[ri]);
                if (iface == null) continue;
                for (int di = 0; di < order.Length; di++)
                {
                    if (order[di] < 0) continue;
                    if (RootPresent(iface, rids[ri], (byte)order[di], log)) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 找“有配对设备在应答”的槽位号。0xFF 是接收器自身，1..6 是配对槽位。
        /// 只问根特征 0x0000，不读电池；返回 -1 表示当前没有任何槽位应答。
        /// </summary>
        public static int FindPresentDevice(HidGroup g, Action<string> log)
        {
            if (g == null || g.Vid != 0x046D) return -1;
            byte[] rids = new byte[] { 0x11, 0x10 };
            for (int ri = 0; ri < rids.Length; ri++)
            {
                HidInterfaceInfo iface = PickInterface(g, rids[ri]);
                if (iface == null) continue;
                for (int di = 1; di <= 6; di++)
                {
                    if (RootPresent(iface, rids[ri], (byte)di, log)) return di;
                }
            }
            return -1;
        }

        /// <summary>
        /// 按报文长度挑接口：HID++ 长报文走 20 字节报告、短报文走 7 字节报告。
        /// 同一个接收器上还有 32 字节的厂商控制口（0xFF00 / usage 0x0004），往它写 20 字节
        /// 报文会被驱动直接拒绝，所以不能只取“第一个输出长度 >= 7”的接口。
        /// 找不到精确长度时只在 <= 20 的接口里退让，绝不落到 32 字节控制口。
        /// </summary>
        private static HidInterfaceInfo PickInterface(HidGroup g, byte rid)
        {
            int want = rid == 0x11 ? 20 : 7;
            HidInterfaceInfo fallback = null;
            for (int i = 0; i < g.Interfaces.Count; i++)
            {
                HidInterfaceInfo it = g.Interfaces[i];
                if (it.OutputLen == want) return it;
                if (it.OutputLen > want && it.OutputLen <= 20
                    && (fallback == null || it.OutputLen < fallback.OutputLen))
                    fallback = it;
            }
            return fallback;
        }

        /// <summary>
        /// 用根特征 0x0000 判断某个设备索引上是否真的有设备：
        /// 空槽位回错误码、接收器自身（0xFF）也回错误码，两者都不算“在场”。
        /// 这样既省查询预算，也不会把“鼠标休眠没人应答”误判成“设备说没有电池”。
        /// </summary>
        private static bool RootPresent(HidInterfaceInfo iface, byte rid, byte deviceIndex, Action<string> log)
        {
            // 在场探测要问 7 个槽位，多数槽位上是空的；用短超时，避免空槽位把首轮扫描拖到 10 秒。
            byte[] reply = Call(iface, rid, deviceIndex, 0x00, 0x00, log, new byte[] { 0x00, 0x00 }, 320);
            if (reply == null || reply.Length < 7) return false;
            if ((reply[3] & 0x80) != 0) return false;
            return true;
        }

        private static BatteryResult ReadPercentage(HidInterfaceInfo iface, byte rid, byte deviceIndex, int featureIndex, int featureId, Action<string> log)
        {
            byte[] reply = Call(iface, rid, deviceIndex, (byte)featureIndex, 0x00, log);
            if (reply == null || reply.Length < 6) return null;
            int level = reply[4];
            int status = reply[5];
            if (level < 0 || level > 100) return null;
            bool? charging = null;
            if (status == 1 || status == 2 || status == 4) charging = true;
            else if (status == 0) charging = false;
            return BatteryResult.Success(level, charging, "罗技 HID++ 2.0",
                string.Format("Feature 0x{0:X4} 电量={1}% 状态={2} 设备索引=0x{3:X2}", featureId, level, status, deviceIndex),
                HidNative.Hex(reply));
        }

        private static BatteryResult ReadVoltage(HidInterfaceInfo iface, byte rid, byte deviceIndex, int featureIndex, Action<string> log)
        {
            byte[] reply = Call(iface, rid, deviceIndex, (byte)featureIndex, 0x00, log);
            if (reply == null || reply.Length < 6) return null;
            // 0x1001 载荷：u16 大端毫伏 + 1 字节 flags。公开文档（OpenLogi 线格式、Linux hid-logitech-hidpp）
            // 都按大端给出，所以这里按大端解码；守卫收敛到锂电真实区间，超出即视为无效读数。
            int mv = (reply[4] << 8) + reply[5];
            if (mv < 1000 || mv > 6000) return null;
            bool? charging = null;
            if (reply.Length > 6)
            {
                // bit7 = 外部供电存在。只有它在位时，低位的充电状态才有意义。
                charging = (reply[6] & 0x80) != 0;
            }
            BatteryResult r = BatteryResult.Fail("罗技 HID++ 电压特征",
                string.Format("Feature 0x{0:X4} 电压={1}mV 设备索引=0x{2:X2}", BatteryVoltageFeature, mv, deviceIndex),
                HidNative.Hex(reply));
            r.HasVoltage = true;
            r.VoltageMv = mv;
            r.Charging = charging;
            return r;
        }

        private static int GetFeatureIndex(HidInterfaceInfo iface, byte rid, byte deviceIndex, int featureId, ref bool answered, Action<string> log)
        {
            byte[] reply = Call(iface, rid, deviceIndex, 0x00, 0x00, log, new byte[] { (byte)(featureId >> 8), (byte)(featureId & 0xFF) });
            if (reply == null) return -1;
            answered = true;
            if (reply.Length < 7) return -1;
            if ((reply[3] & 0x80) != 0) return -1;
            // HID++ 2.0 的 getFeature 应答固定是：
            //   byte4 = 特性索引（0 表示该设备没有这个特性）、byte5 = 特性类型、byte6 = 特性版本。
            // 索引只能取 byte4：byte6 是版本号，早先“从高位往低找第一个非零字节”会把版本号
            // 当成索引（0x1001 会被读成 2 而不是 6），电压特征因此永远定位不到。
            int index = reply[4];
            if (index <= 0) return -1;
            return index;
        }

        private static byte[] Call(HidInterfaceInfo iface, byte rid, byte deviceIndex, byte featureIndex, byte function, Action<string> log)
        {
            return Call(iface, rid, deviceIndex, featureIndex, function, log, null);
        }

        /// <summary>HID++ 2.0 请求里的软件号，1..15 轮换，用来把“哪条请求的应答”对上。</summary>
        private static int _swId;

        private static byte[] Call(HidInterfaceInfo iface, byte rid, byte deviceIndex, byte featureIndex, byte function, Action<string> log, byte[] parameters) { return Call(iface, rid, deviceIndex, featureIndex, function, log, parameters, 400); }

        /// <summary>带读超时的版本：在场探测只需要很短的时间窗（有设备几十毫秒内就回），
        /// 用 400ms 等 7 个空槽位会把首轮扫描拖到 10 秒。</summary>
        private static byte[] Call(HidInterfaceInfo iface, byte rid, byte deviceIndex, byte featureIndex, byte function, Action<string> log, byte[] parameters, int readTimeoutMs)
        {
            // HID 报告的缓冲区长度必须等于该报告本身的长度：20 字节报文要发在 20 字节报告上，
            // 7 字节短报文发在 7 字节报告上。写错长度驱动会直接拒绝（WriteOutput 返回 false）。
            int len = iface.OutputLen > 0 ? iface.OutputLen : (rid == 0x11 ? 20 : 7);
            _swId++;
            if (_swId > 15) _swId = 1;
            byte address = (byte)(((function & 0x0F) << 4) | _swId);
            byte[] req = new byte[len];
            req[0] = rid;
            req[1] = deviceIndex;
            req[2] = featureIndex;
            req[3] = address;
            if (parameters != null)
            {
                for (int i = 0; i < parameters.Length && 4 + i < len; i++) req[4 + i] = parameters[i];
            }
            // 同一个接口上连续发请求时，上一轮的应答可能还在缓冲里；先清掉，
            // 否则会把旧报文当成本次结果（特性索引会整体错位一格）。
            // 被动监听线程也要让开：应答只会送到正在读的那个句柄，两边同时读会互相抢。
            BatteryService.LinkPauseUntilTicks = DateTime.UtcNow.AddMilliseconds(1500).Ticks;
            HidNative.ReadInputReports(iface, 30, 16);
            bool ok = HidNative.WriteOutput(iface, req, 500);
            if (!ok) return null;
            List<byte[]> reps = HidNative.ReadInputReports(iface, readTimeoutMs, 8);
            for (int i = 0; i < reps.Count; i++)
            {
                byte[] r = reps[i];
                log("HID++ RX: " + HidNative.Hex(r));
                // 软件号逐次轮换，所以地址字节一致就一定是本次请求的应答；
                // 错误应答的地址字节带 0x80 位，同样算“设备理我了”。
                if (r.Length >= 4 && r[0] == rid && r[1] == deviceIndex && r[2] == featureIndex
                    && (r[3] == address || (r[3] & 0x80) != 0))
                    return r;
            }
            return null;
        }
    }
}
