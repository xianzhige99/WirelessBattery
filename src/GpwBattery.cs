using System;
using System.Collections.Generic;

namespace WirelessBattery
{
    /// <summary>
    /// GPW 单台设备的滤波与显示状态。只存内存：重启后从新采样重新积累（ini 里只留配置与观测值）。
    /// </summary>
    public class GpwFilterState
    {
        public List<double> Window = new List<double>();
        public double Ema = -1;
        public int Displayed = -1;
        public int Pending = -1;
        public int PendingCount;
        public double LowMv = -1;
        public int RiseCount;
        public bool HasExternalPower;
        public bool SeenExternalPower;
        public DateTime ChargeChangedUtc = DateTime.MinValue;
    }

    /// <summary>
    /// 罗技 HID++ 电压特征（0x1001）到百分比的换算与显示平滑。
    /// 纯算法：不打开设备、不碰界面，便于用电压序列直接断言输出序列。
    ///
    /// 曲线形状是内置的归一化锂电开路电压表（10 个点、分段线性），
    /// 不拟合、不推导公式：满/空锚点只做线性缩放，中段偏移只做三角权重平移。
    /// </summary>
    public static class GpwBattery
    {
        /// <summary>绝对下限：低于它直接按 0% 并标记异常，不参与滤波。</summary>
        public const double HardFloorMv = 3000;

        /// <summary>归一化电压（0=空电锚点，1=满电锚点）。</summary>
        private static readonly double[] CurveU = new double[] { 0.0, 0.11, 0.22, 0.33, 0.44, 0.56, 0.67, 0.78, 0.89, 1.0 };
        /// <summary>与 CurveU 一一对应的内置归一化锂电曲线（百分比）。</summary>
        private static readonly double[] CurvePct = new double[] { 0, 5, 10, 20, 35, 50, 65, 80, 90, 100 };

        /// <summary>归一化电压 → 百分比（含中段偏移），不涉及滤波。</summary>
        public static int CurvePercent(GpwConfig cfg, double mv)
        {
            if (cfg == null) cfg = new GpwConfig();
            double span = cfg.FullMv - cfg.EmptyMv;
            if (span <= 0) span = 900;
            double u = (mv - cfg.EmptyMv) / span;
            if (u < 0) u = 0;
            if (u > 1) u = 1;

            double pct = CurvePct[CurvePct.Length - 1];
            for (int i = 0; i < CurveU.Length - 1; i++)
            {
                if (u <= CurveU[i + 1])
                {
                    double seg = CurveU[i + 1] - CurveU[i];
                    double t = seg <= 0 ? 0 : (u - CurveU[i]) / seg;
                    pct = CurvePct[i] + (CurvePct[i + 1] - CurvePct[i]) * t;
                    break;
                }
            }

            // 中段偏移：三角权重（两端 0、中间 1），最多 ±10%。
            double offset = cfg.MidOffset;
            if (offset < -10) offset = -10;
            if (offset > 10) offset = 10;
            double centered = 2.0 * u - 1.0;
            if (centered < 0) centered = -centered;
            double weight = 1.0 - centered;
            pct += offset * weight;

            if (pct < 0) pct = 0;
            if (pct > 100) pct = 100;
            return (int)Math.Round(pct);
        }

        /// <summary>
        /// 喂一个采样。返回应当显示的百分比；-1 表示“还没有可显示的值”（首次采样在充电、或滤波尚未产出）。
        /// charging=true 表示外部供电有效：此时跳过换算，返回上次显示的百分比。
        /// </summary>
        public static int Feed(GpwConfig cfg, GpwFilterState st, double mv, bool externalPower,
            DateTime now, out bool charging, out bool anomaly, out double smoothedMv)
        {
            charging = externalPower;
            anomaly = false;
            smoothedMv = st == null ? mv : st.Ema;

            if (st == null) return -1;
            if (cfg == null) cfg = new GpwConfig();

            // 外部供电 / 拔线的状态切换：清掉滤波历史，避免表面电荷造成跳变。
            if (!st.HasExternalPower && externalPower) st.SeenExternalPower = true;
            if (st.HasExternalPower != externalPower)
            {
                st.HasExternalPower = externalPower;
                st.ChargeChangedUtc = now;
                if (!externalPower) cfg.CycleCount++;
                st.Window.Clear();
                st.Ema = -1;
                st.Pending = -1;
                st.PendingCount = 0;
                st.LowMv = -1;
                st.RiseCount = 0;
            }

            // 绝对下限保护：不换算、不滤波，直接 0% + 异常。
            if (mv < HardFloorMv)
            {
                anomaly = true;
                st.Displayed = 0;
                st.Window.Clear();
                st.Ema = -1;
                st.Pending = -1;
                st.PendingCount = 0;
                smoothedMv = mv;
                return 0;
            }

            // 充电中：不映射，保留上次显示（首次采样就充电则没有可显示的百分比）。
            if (externalPower) return st.Displayed;

            // 1) 中值滤波（窗口 1..7）
            st.Window.Add(mv);
            int win = cfg.MedianWindow;
            if (win < 1) win = 1;
            if (win > 7) win = 7;
            while (st.Window.Count > win) st.Window.RemoveAt(0);
            double median = Median(st.Window);

            // 2) EMA
            double alpha = cfg.Alpha;
            if (alpha < 0.1) alpha = 0.1;
            if (alpha > 0.5) alpha = 0.5;
            if (st.Ema < 0) st.Ema = median;
            else st.Ema = st.Ema + alpha * (median - st.Ema);
            smoothedMv = st.Ema;

            int candidate = CurvePercent(cfg, st.Ema);

            if (st.Displayed < 0)
            {
                st.Displayed = candidate;
                if (st.LowMv < 0 || mv < st.LowMv) st.LowMv = mv;
                return candidate;
            }

            // 3) 放电态只降不升：除非原始电压持续回升超过容差（连续 2 次站稳）。
            if (st.LowMv < 0 || mv < st.LowMv) st.LowMv = mv;
            if (candidate > st.Displayed)
            {
                double rise = mv - st.LowMv;
                double tolerance = cfg.RiseTolerance;
                if (tolerance < 0) tolerance = 0;
                if (rise >= tolerance)
                {
                    st.RiseCount++;
                    if (st.RiseCount >= 2)
                    {
                        st.RiseCount = 0;
                        st.LowMv = mv;
                    }
                    else
                    {
                        return st.Displayed;
                    }
                }
                else
                {
                    st.RiseCount = 0;
                    return st.Displayed;
                }
            }

            // 4) 滞回死区 + 持续偏离才刷新（防止甩动时数字乱跳）。
            double deadband = cfg.Hysteresis;
            if (deadband < 0) deadband = 0;
            int delta = candidate - st.Displayed;
            if (delta < 0) delta = -delta;
            if (delta < deadband)
            {
                st.Pending = -1;
                st.PendingCount = 0;
                return st.Displayed;
            }

            int gap = candidate - st.Pending;
            if (gap < 0) gap = -gap;
            if (st.Pending >= 0 && gap <= 1) st.PendingCount++;
            else { st.Pending = candidate; st.PendingCount = 1; }

            if (st.PendingCount >= 2)
            {
                st.Displayed = candidate;
                st.Pending = -1;
                st.PendingCount = 0;
            }
            return st.Displayed;
        }

        /// <summary>
        /// 观测最高非充电电压（只读展示，不参与换算）。
        /// 采样条件：非充电、距上次充电状态变化超过 5 分钟、读数高于绝对下限。
        /// </summary>
        public static void Observe(GpwConfig cfg, GpwFilterState st, double mv, bool externalPower, DateTime now)
        {
            if (cfg == null || st == null) return;
            if (externalPower) return;
            if (st.ChargeChangedUtc != DateTime.MinValue && now - st.ChargeChangedUtc < TimeSpan.FromMinutes(5)) return;
            if (mv < HardFloorMv) return;
            if (mv > cfg.ObservedMaxMv) cfg.ObservedMaxMv = mv;
            cfg.Samples++;
        }

        /// <summary>
        /// 建议的满电锚点；0 表示当前不满足提示条件（样本 &lt; 5 或观测值不比设定锚点低 50mV 以上）。
        /// </summary>
        public static int SuggestFullMv(GpwConfig cfg)
        {
            if (cfg == null) return 0;
            if (cfg.Samples < 5) return 0;
            if (cfg.ObservedMaxMv + 50 >= cfg.FullMv) return 0;
            int target = (int)(Math.Round(cfg.ObservedMaxMv / 10.0) * 10);
            if (target >= cfg.FullMv) return 0;
            if (target <= cfg.EmptyMv + 100) return 0;
            return target;
        }

        /// <summary>
        /// 全自动下调：默认关闭；只允许下调、每次 50mV、下限 4000mV、至少隔一个充电周期、样本 ≥ 5、手动改过即停。
        /// 返回 true 表示这次真的调了（调用方负责落盘与提示）。
        /// </summary>
        public static bool TryAutoAdjust(GpwConfig cfg)
        {
            if (cfg == null) return false;
            if (!cfg.AutoAdjust || cfg.ManualEdited) return false;
            if (cfg.Samples < 5) return false;
            if (cfg.ObservedMaxMv + 50 >= cfg.FullMv) return false;
            if (cfg.FullMv - 50 < 4000) return false;
            // 首次自动下调没有“上次周期”可比，允许直接下调一次；之后必须隔一个充电周期。
            if (cfg.LastAutoAdjustUtc != DateTime.MinValue && cfg.CycleCount - cfg.LastAutoCycle < 1) return false;
            cfg.FullMv -= 50;
            cfg.LastAutoCycle = cfg.CycleCount;
            cfg.LastAutoAdjustUtc = DateTime.UtcNow;
            return true;
        }

        private static double Median(List<double> values)
        {
            int n = values.Count;
            if (n == 0) return 0;
            double[] copy = new double[n];
            for (int i = 0; i < n; i++) copy[i] = values[i];
            for (int i = 1; i < n; i++)
            {
                double v = copy[i];
                int j = i - 1;
                while (j >= 0 && copy[j] > v)
                {
                    copy[j + 1] = copy[j];
                    j--;
                }
                copy[j + 1] = v;
            }
            return copy[n / 2];
        }
    }
}
