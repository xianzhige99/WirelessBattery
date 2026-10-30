using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace WirelessBattery
{
    /// <summary>
    /// 不依赖硬件的自检：阶梯段时长、蓝牙阶梯估算、电压换算与滤波、配置读写、键名白名单。
    /// 由 tests\run-tests.ps1 编译成 bin\tests\SelfTest.exe 后运行，退出码 0 表示全部通过。
    /// 只读硬件之外的纯逻辑，因此可以在任何一台 Windows 上重复执行。
    /// </summary>
    internal static class SelfTestProgram
    {
        private static int _pass;
        private static int _fail;
        private static readonly List<string> _failures = new List<string>();

        private static int Main()
        {
            // 不强制改控制台编码：沿用宿主控制台的代码页，避免中文在 cmd / Windows Terminal 之间来回乱码。
            Console.WriteLine("WirelessBattery self test");
            Console.WriteLine("exe: " + Application.ExecutablePath);
            Console.WriteLine();

            TestSegmentHours();
            TestStageValidation();
            TestWeights();
            TestGpwCurve();
            TestGpwFeed();
            TestConfigRoundTrip();
            TestSafeDeviceId();
            TestEstimateBt();
            TestGpwValidation();
            TestSanitizeName();
            TestK02BSJudge();

            Console.WriteLine();
            Console.WriteLine("PASS " + _pass + "  FAIL " + _fail);
            if (_fail > 0)
            {
                Console.WriteLine("失败项：");
                for (int i = 0; i < _failures.Count; i++) Console.WriteLine("  - " + _failures[i]);
                return 1;
            }
            Console.WriteLine("ALL PASS");
            return 0;
        }

        // ---------------- 断言 ----------------

        private static void Check(bool ok, string name, string detail)
        {
            if (ok)
            {
                _pass++;
                Console.WriteLine("  PASS  " + name);
            }
            else
            {
                _fail++;
                _failures.Add(name + " -> " + detail);
                Console.WriteLine("  FAIL  " + name + "  (" + detail + ")");
            }
        }

        private static void Near(double actual, double expected, double tolerance, string name)
        {
            Check(Math.Abs(actual - expected) <= tolerance, name,
                string.Format(CultureInfo.InvariantCulture, "期望 {0:0.###}，实际 {1:0.###}", expected, actual));
        }

        private static void Equal(int actual, int expected, string name)
        {
            Check(actual == expected, name,
                string.Format(CultureInfo.InvariantCulture, "期望 {0}，实际 {1}", expected, actual));
        }

        // ---------------- 阶梯段时长 ----------------

        private static void TestSegmentHours()
        {
            Console.WriteLine("[阶梯段时长]");
            EstimateConfig cfg = new EstimateConfig();
            cfg.FullHours = 163;

            // 默认权重 8,40,40,8,4（总和 100），对应 100-80 / 80-60 / 60-40 / 40-20 / 20-0
            Near(AppSettings.SegmentHours(cfg, 100, 80), 163 * 0.08, 0.01, "100→80 占满电时间 8%");
            Near(AppSettings.SegmentHours(cfg, 80, 60), 163 * 0.40, 0.01, "80→60 占 40%");
            Near(AppSettings.SegmentHours(cfg, 60, 40), 163 * 0.40, 0.01, "60→40 占 40%");
            Near(AppSettings.SegmentHours(cfg, 40, 20), 163 * 0.08, 0.01, "40→20 占 8%");
            Near(AppSettings.SegmentHours(cfg, 20, 0), 163 * 0.04, 0.01, "20→0 占 4%");

            double sum = 0;
            int[] highs = new int[] { 100, 80, 60, 40, 20 };
            int[] lows = new int[] { 80, 60, 40, 20, 0 };
            for (int i = 0; i < highs.Length; i++) sum += AppSettings.SegmentHours(cfg, highs[i], lows[i]);
            Near(sum, 163, 0.05, "五段之和 = 满电小时数");

            // 非标准阶梯：权重默认（8,40,40,8,4）时中段 60-40 权重最高，
            // 因此 100,50,0 的“50%”并不在中点，上半程应占 68/100（8+40+20）。
            EstimateConfig odd = new EstimateConfig();
            odd.FullHours = 100;
            odd.Stages = new List<int>(new int[] { 100, 50, 0 });
            Near(AppSettings.SegmentHours(odd, 100, 50), 68, 0.01, "默认权重下 100→50 占 68 小时（重段在 60-40）");
            Near(AppSettings.SegmentHours(odd, 50, 0), 32, 0.01, "默认权重下 50→0 占 32 小时");

            // 权重均匀时，两个半程才各占一半——证明时长确实由权重决定，而不是简单对半分。
            EstimateConfig flat = new EstimateConfig();
            flat.FullHours = 100;
            flat.Stages = new List<int>(new int[] { 100, 50, 0 });
            flat.Weights = new List<double>(new double[] { 20, 20, 20, 20, 20 });
            Near(AppSettings.SegmentHours(flat, 100, 50), 50, 0.01, "均匀权重下 100→50 = 50 小时");
            Near(AppSettings.SegmentHours(flat, 50, 0), 50, 0.01, "均匀权重下 50→0 = 50 小时");

            // 非法输入不应崩、不应返回负数
            Near(AppSettings.SegmentHours(cfg, 40, 40), 0, 0.0001, "reported == next 返回 0");
            Near(AppSettings.SegmentHours(null, 100, 80), 0, 0.0001, "cfg 为空返回 0");
        }

        private static void TestStageValidation()
        {
            Console.WriteLine("[阶梯校验]");
            EstimateConfig ok = new EstimateConfig();
            string reason;
            Check(AppSettings.IsValidStageSet(ok, out reason), "默认阶梯 100,80,60,40,20,0 合法", reason);

            EstimateConfig ascending = new EstimateConfig();
            ascending.Stages = new List<int>(new int[] { 80, 100 });
            Check(!AppSettings.IsValidStageSet(ascending, out reason), "升序阶梯被拒", "应判为非法");

            EstimateConfig duplicate = new EstimateConfig();
            duplicate.Stages = new List<int>(new int[] { 100, 100 });
            Check(!AppSettings.IsValidStageSet(duplicate, out reason), "重复档位被拒", "应判为非法");

            EstimateConfig outOfRange = new EstimateConfig();
            outOfRange.Stages = new List<int>(new int[] { 120, 50 });
            Check(!AppSettings.IsValidStageSet(outOfRange, out reason), "超出 0~100 被拒", "应判为非法");

            EstimateConfig single = new EstimateConfig();
            single.Stages = new List<int>(new int[] { 100 });
            Check(!AppSettings.IsValidStageSet(single, out reason), "单档被拒", "应判为非法");
        }

        private static void TestWeights()
        {
            Console.WriteLine("[权重解析]");
            Check(AppSettings.ParseWeights("8,40,40,8,4") != null, "五项权重可解析", "返回 null");
            Check(AppSettings.ParseWeights("8,40,40,8") == null, "四项权重被拒", "应返回 null");
            Check(AppSettings.ParseWeights("8,0,40,8,4") == null, "含 0 的权重被拒", "应返回 null");
            Check(AppSettings.ParseWeights("8，40，40，8，4") != null, "全角逗号可解析", "返回 null");

            double[] shares = AppSettings.NormalizedShares(new List<double>(new double[] { 8, 40, 40, 8, 4 }));
            Near(shares[1], 0.4, 0.0001, "80-60 段占比 40%");
        }

        // ---------------- 电压换算与滤波 ----------------

        private static void TestGpwCurve()
        {
            Console.WriteLine("[电压→百分比]");
            GpwConfig cfg = new GpwConfig();
            Equal(GpwBattery.CurvePercent(cfg, 4200), 100, "满电锚点 4200mV = 100%");
            Equal(GpwBattery.CurvePercent(cfg, 3300), 0, "空电锚点 3300mV = 0%");
            Equal(GpwBattery.CurvePercent(cfg, 5000), 100, "超上限被夹到 100%");
            Equal(GpwBattery.CurvePercent(cfg, 2000), 0, "超下限被夹到 0%");

            bool monotonic = true;
            int prev = -1;
            for (int mv = 3300; mv <= 4200; mv += 10)
            {
                int pct = GpwBattery.CurvePercent(cfg, mv);
                if (pct < prev) { monotonic = false; break; }
                prev = pct;
            }
            Check(monotonic, "3300~4200mV 全程单调不减", "出现回落");

            GpwConfig offset = new GpwConfig();
            offset.MidOffset = 10;
            int mid = GpwBattery.CurvePercent(offset, 3750);
            int plain = GpwBattery.CurvePercent(cfg, 3750);
            Check(mid > plain, "中段 +10% 偏移抬高中段读数", "偏移未生效");
            Equal(GpwBattery.CurvePercent(offset, 4200), 100, "端点不受中段偏移影响");
        }

        private static void TestGpwFeed()
        {
            Console.WriteLine("[电压滤波]");
            GpwConfig cfg = new GpwConfig();
            DateTime now = DateTime.UtcNow;
            bool charging, anomaly;
            double smoothed;

            GpwFilterState st = new GpwFilterState();
            int first = GpwBattery.Feed(cfg, st, 4000, false, now, out charging, out anomaly, out smoothed);
            Check(first >= 0 && !charging && !anomaly, "首次采样给出百分比而非 -1", "first=" + first);

            int hold = GpwBattery.Feed(cfg, st, 4200, true, now, out charging, out anomaly, out smoothed);
            Check(charging, "外部供电标记为充电中", "charging=false");
            Equal(hold, first, "充电中沿用上次显示值");

            GpwFilterState low = new GpwFilterState();
            int zero = GpwBattery.Feed(cfg, low, 2900, false, now, out charging, out anomaly, out smoothed);
            Check(anomaly && zero == 0, "低于 3000mV 判异常并按 0%", "anomaly=" + anomaly + " level=" + zero);

            // 放电态：小幅回升不应让数字跳上去
            GpwFilterState rise = new GpwFilterState();
            for (int i = 0; i < 6; i++) GpwBattery.Feed(cfg, rise, 3800, false, now, out charging, out anomaly, out smoothed);
            int before = rise.Displayed;
            int after = GpwBattery.Feed(cfg, rise, 3810, false, now, out charging, out anomaly, out smoothed);
            Check(after >= before - cfg.Hysteresis, "小幅回升不产生向下跳变", "before=" + before + " after=" + after);

            GpwConfig empty = new GpwConfig();
            empty.FullMv = 4200;
            empty.EmptyMv = 3300;
            GpwFilterState s2 = new GpwFilterState();
            bool dropped = true;
            for (int i = 0; i < 40; i++)
            {
                int pct = GpwBattery.Feed(empty, s2, 4000 - i * 15, false, now, out charging, out anomaly, out smoothed);
                if (pct < 0) { dropped = false; break; }
            }
            Check(dropped, "连续放电 40 轮读数始终有效", "出现 -1");
        }

        // ---------------- 配置读写 ----------------

        private static void TestConfigRoundTrip()
        {
            Console.WriteLine("[配置读写]");
            string dir = Path.Combine(Path.GetTempPath(), "wb_selftest_" + System.Diagnostics.Process.GetCurrentProcess().Id);
            string path = Path.Combine(dir, "settings.ini");
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);

                AppSettings a = new AppSettings();
                a.IntervalSeconds = 45;
                a.CloseToTray = false;
                a.AutoStartWanted = false;
                a.K02BSSoleDongleAsConnected = true;
                a.SetDeviceEnabled("BT:TEST HEADSET", false);
                a.SetDeviceName("MCHOSE", "我的鼠标");
                a.GetEstimateConfig("K02BS").FullHours = 77;
                a.GetGpwConfig().FullMv = 4150;
                a.Probes["3837_100A"] = new ProbeRecord();
                a.Probes["3837_100A"].Protocol = ProtocolKind.MchoseFeature;
                a.Probes["3837_100A"].ValueKind = ProbeValueKind.Percentage;
                a.Probes["3837_100A"].Firmware = 5173;
                a.Probes["3837_100A"].StampUtc = DateTime.UtcNow;
                a.Probes["3837_100A"].UntilUtc = DateTime.UtcNow.AddDays(1);

                Check(ConfigStore.Save(path, a), "写盘成功", "Save 返回 false");
                Check(!File.Exists(path + ".tmp"), "不残留 .tmp 临时文件", "发现 .tmp");

                AppSettings b = new AppSettings();
                ConfigStore.Load(path, b);

                Equal(b.IntervalSeconds, 45, "Interval 往返一致");
                Check(!b.CloseToTray, "CloseToTray 往返一致", "实际为 true");
                Check(!b.AutoStartWanted, "AutoStartWanted 往返一致", "实际为 true");
                Check(b.K02BSSoleDongleAsConnected, "K02BS 严格口径开关往返一致", "实际为 false");
                Check(!b.IsDeviceEnabled("BT:TEST HEADSET"), "未勾选状态往返一致", "实际为已勾选");
                Check(b.GetDeviceName("MCHOSE") == "我的鼠标", "自定义设备名往返一致", "实际为空或不同");
                Near(b.GetEstimateConfig("K02BS").FullHours, 77, 0.001, "满电小时往返一致");
                Near(b.GetGpwConfig().FullMv, 4150, 0.001, "电压锚点往返一致");

                ProbeRecord pr;
                Check(b.Probes.TryGetValue("3837_100A", out pr) && pr.Protocol == ProtocolKind.MchoseFeature,
                    "协议路由缓存往返一致", "缓存丢失或协议不同");
                Check(pr != null && pr.Firmware == 5173, "固件指纹往返一致", "指纹丢失");
            }
            catch (Exception ex)
            {
                Check(false, "配置读写不抛异常", ex.Message);
            }
            finally
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            }
        }

        private static void TestSafeDeviceId()
        {
            Console.WriteLine("[型号级键白名单]");
            Check(ConfigStore.IsSafeDeviceId("MCHOSE"), "MCHOSE 合法", "被拒");
            Check(ConfigStore.IsSafeDeviceId("K02BS"), "K02BS 合法", "被拒");
            Check(ConfigStore.IsSafeDeviceId("046D:C539"), "046D:C539 合法", "被拒");
            Check(ConfigStore.IsSafeDeviceId("BT:XIBERIA K02BS"), "BT:<名称> 合法", "被拒");

            Check(!ConfigStore.IsSafeDeviceId(@"HID\VID_046D&PID_C539&MI_02"), "实例路径被拒", "应被拒");
            Check(!ConfigStore.IsSafeDeviceId(@"USB\VID_046D&PID_C539"), "USB 实例路径被拒", "应被拒");
            Check(!ConfigStore.IsSafeDeviceId("BTHENUM#{0000}#7"), "BTHENUM 实例 ID 被拒", "应被拒");
            Check(!ConfigStore.IsSafeDeviceId("{a45c254e-df1c-4efd-8020-67d146a850e0}"), "GUID 键被拒", "应被拒");
            Check(!ConfigStore.IsSafeDeviceId(""), "空键被拒", "应被拒");
        }

        private static void TestEstimateBt()
        {
            Console.WriteLine("[蓝牙阶梯估算]");
            AppSettings s = new AppSettings();   // K02BS 默认开启估算、163 小时
            double used;
            int next;
            bool invalid;
            string reason;
            int projected;

            int shown = s.EstimateBt("K02BS", 80, out used, out next, out invalid, out reason, out projected);
            Check(!invalid, "上报 80% 属于阶梯，通过校验", reason);
            Check(shown >= next && shown <= 80, "推算值落在 [下一档, 上报值]", "shown=" + shown + " next=" + next);
            Equal(next, 60, "80% 的下一档是 60%");

            int bad = s.EstimateBt("K02BS", 45, out used, out next, out invalid, out reason, out projected);
            Check(invalid, "上报 45% 不在阶梯，判为设置有误", "未判为非法");
            Equal(bad, 45, "非法上报值原样显示");
            Check(!string.IsNullOrEmpty(reason), "非法时有原因文案", "原因为空");

            // 上报值跳档：锚点重置，已保持时间归零
            s.EstimateBt("K02BS", 60, out used, out next, out invalid, out reason, out projected);
            Near(used, 0, 0.001, "阶梯跳变后重新计时");
        }

        private static void TestGpwValidation()
        {
            Console.WriteLine("[电压参数校验]");
            string reason;
            Check(AppSettings.IsValidGpw(new GpwConfig(), out reason), "默认参数合法", reason);

            GpwConfig bad = new GpwConfig();
            bad.EmptyMv = 4300;
            Check(!AppSettings.IsValidGpw(bad, out reason), "空电≥满电被拒", "应被拒");

            GpwConfig tooHigh = new GpwConfig();
            tooHigh.FullMv = 5000;
            Check(!AppSettings.IsValidGpw(tooHigh, out reason), "满电锚点超 4500mV 被拒", "应被拒");

            GpwConfig alpha = new GpwConfig();
            alpha.Alpha = 0.9;
            Check(!AppSettings.IsValidGpw(alpha, out reason), "EMA α 超 0.5 被拒", "应被拒");
        }

        private static void TestSanitizeName()
        {
            Console.WriteLine("[设备名清洗]");
            Check(ConfigStore.SanitizeName("  A\r\nB\tC  ") == "ABC", "剥离换行/制表符并去首尾空白", "结果不符合预期");
            Check(ConfigStore.SanitizeName("   ") == "", "纯空白视为空", "未清空");
            Check(ConfigStore.SanitizeName(null) == "", "null 视为空", "未返回空串");
            string longName = new string('长', 40);
            Check(ConfigStore.SanitizeName(longName).Length == ConfigStore.MaxDeviceNameLength,
                "超长名截断到 32 字", "长度不一致");
        }

        /// <summary>
        /// K02BS 链路判定真值表。两条硬约束：
        ///   1) 只有接收器在线（dongle=true、蓝牙=false）默认不能算「已连接」；
        ///   2) HFP 电量属性不算连接证据——关机后它仍会残留，不能进 Decide 的输入。
        /// </summary>
        private static void TestK02BSJudge()
        {
            Console.WriteLine("[K02BS 链路判定]");

            Equal((int)K02BSLinkJudge.Decide(false, false), (int)K02BSLinkState.None, "都没有 → None");
            Equal((int)K02BSLinkJudge.Decide(true, false), (int)K02BSLinkState.DongleOnly, "只有接收器 → DongleOnly");
            Equal((int)K02BSLinkJudge.Decide(false, true), (int)K02BSLinkState.Bluetooth, "蓝牙链路通 → Bluetooth");
            Equal((int)K02BSLinkJudge.Decide(true, true), (int)K02BSLinkState.Dual, "接收器 + 蓝牙 → Dual");

            Check(!K02BSLinkJudge.CountsAsConnected(K02BSLinkState.None, false), "None 不算已连接", "应为 false");
            Check(!K02BSLinkJudge.CountsAsConnected(K02BSLinkState.None, true), "None 即使放宽也不算已连接", "应为 false");
            Check(!K02BSLinkJudge.CountsAsConnected(K02BSLinkState.DongleOnly, false),
                "严格口径：只有接收器不算已连接（耳机关机不骗人）", "应为 false");
            Check(K02BSLinkJudge.CountsAsConnected(K02BSLinkState.DongleOnly, true),
                "放宽口径：只有接收器也算已连接（2.4G 专用场景）", "应为 true");
            Check(K02BSLinkJudge.CountsAsConnected(K02BSLinkState.Bluetooth, false), "蓝牙连接算已连接", "应为 false");
            Check(K02BSLinkJudge.CountsAsConnected(K02BSLinkState.Dual, false), "双模算已连接", "应为 false");

            Check(K02BSLinkJudge.ModeText(K02BSLinkState.Dual) == "双模", "Dual 文案为双模", "文案不符");
            Check(K02BSLinkJudge.ModeText(K02BSLinkState.DongleOnly) == "2.4G", "DongleOnly 文案为 2.4G", "文案不符");
            Check(K02BSLinkJudge.ModeText(K02BSLinkState.None) == "未连接", "None 文案为未连接", "文案不符");
        }
    }
}
