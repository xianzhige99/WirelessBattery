using System;
using System.Collections.Generic;
using System.Globalization;

namespace WirelessBattery
{
    /// <summary>
    /// 单个罗技设备型号的电池模型，数据取自 G HUB 网页版 <c>/depots/&lt;model&gt;/battery.xml</c>。
    ///
    /// 与原先的通用锂电曲线不同，这里的电量是「能量积分」的结果：
    /// 原始表是 (时间, 电压, 累计放电 mWh)，按总能量归一后得到 电压 → 剩余电量 的单调曲线。
    /// 生成阶段已经算好 SoC，运行时只做一次分段线性插值——等价于 G HUB 的换算，但不需要累计能量。
    ///
    /// 充电走另一套：充电曲线（电压 → 百分比）负责 4.1V 以下，
    /// 电压进入平台区（stable_state_detection_voltage）后改用「充电时间表」按分钟推进，
    /// 这就是 G HUB 充电时数字会继续涨、且涨得很慢的原因。
    /// </summary>
    public sealed class LogitechBatteryModel
    {
        public int Pid { get; private set; }
        public string Name { get; private set; }
        /// <summary>充电时间表的启用电压（mV）；0 表示没有该字段，充电开始即可用时间表。</summary>
        public double StableMv { get; private set; }
        /// <summary>电压稳定后每 30 秒最多补多少个百分点（gap_fill_speed）。</summary>
        public double GapFill { get; private set; }

        private double[] _dischargeMv;
        private double[] _dischargePct;
        private double[] _chargeMv;
        private double[] _chargePct;
        private double[] _slotMin;
        private double[] _slotPct;

        private static readonly Dictionary<int, LogitechBatteryModel> Cache = new Dictionary<int, LogitechBatteryModel>();

        /// <summary>按设备 PID 取模型；没有对应数据时返回 null（调用方回落到通用曲线）。</summary>
        public static LogitechBatteryModel For(int pid)
        {
            if (pid <= 0) return null;
            lock (Cache)
            {
                LogitechBatteryModel hit;
                if (Cache.TryGetValue(pid, out hit)) return hit;
                LogitechBatteryData.Model raw = FindRaw(pid);
                if (raw == null)
                {
                    Cache[pid] = null;
                    return null;
                }
                LogitechBatteryModel m = new LogitechBatteryModel();
                m.Pid = raw.Pid;
                m.Name = raw.Name;
                m.StableMv = raw.StableMv;
                m.GapFill = raw.GapFill;
                m.ParseDischarge(raw.Discharge);
                ParsePairs(raw.Charge, out m._chargeMv, out m._chargePct);
                ParsePairs(raw.Slots, out m._slotMin, out m._slotPct);
                if (m._dischargeMv == null || m._dischargeMv.Length < 2)
                {
                    Cache[pid] = null;
                    return null;
                }
                Cache[pid] = m;
                return m;
            }
        }

        /// <summary>
        /// 该 PID 是否有对应的电池模型。0x0003 的应答里可能同时出现多个 ID
        /// （规范位、无线 PID、有线 PID），用它来认出真正的设备 PID。
        /// </summary>
        public static bool IsKnownModelPid(int pid)
        {
            if (pid <= 0) return false;
            LogitechBatteryData.Model[] all = LogitechBatteryData.Models;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].Pid == pid) return true;
            }
            return false;
        }

        private static LogitechBatteryData.Model FindRaw(int pid)
        {
            LogitechBatteryData.Model[] all = LogitechBatteryData.Models;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].Pid == pid) return all[i];
            }
            return null;
        }

        private void ParseDischarge(string encoded)
        {
            if (string.IsNullOrEmpty(encoded)) return;
            string[] parts = encoded.Split(';');
            List<double> mv = new List<double>(parts.Length);
            List<double> pct = new List<double>(parts.Length);
            for (int i = 0; i < parts.Length; i++)
            {
                int colon = parts[i].IndexOf(':');
                if (colon <= 0) continue;
                int v;
                int s;
                if (!int.TryParse(parts[i].Substring(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) continue;
                if (!int.TryParse(parts[i].Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out s)) continue;
                mv.Add(v);
                pct.Add(s / 10.0);
            }
            _dischargeMv = mv.ToArray();
            _dischargePct = pct.ToArray();
        }

        private static void ParsePairs(string encoded, out double[] x, out double[] y)
        {
            if (string.IsNullOrEmpty(encoded))
            {
                x = null;
                y = null;
                return;
            }
            string[] parts = encoded.Split(';');
            List<double> xs = new List<double>(parts.Length);
            List<double> ys = new List<double>(parts.Length);
            for (int i = 0; i < parts.Length; i++)
            {
                int colon = parts[i].IndexOf(':');
                if (colon <= 0) continue;
                double a;
                double b;
                if (!double.TryParse(parts[i].Substring(0, colon), NumberStyles.Float, CultureInfo.InvariantCulture, out a)) continue;
                if (!double.TryParse(parts[i].Substring(colon + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out b)) continue;
                xs.Add(a);
                ys.Add(b);
            }
            x = xs.ToArray();
            y = ys.ToArray();
        }

        /// <summary>放电态：电压 → 剩余电量（0..100）。超出曲线范围按端点取值。</summary>
        public double PercentFromDischarge(double mv)
        {
            if (_dischargeMv == null || _dischargeMv.Length < 2) return 0;
            return Interpolate(_dischargeMv, _dischargePct, mv);
        }

        public bool HasChargeCurve
        {
            get { return _chargeMv != null && _chargeMv.Length >= 2; }
        }

        public bool HasSlots
        {
            get { return _slotMin != null && _slotMin.Length >= 1; }
        }

        /// <summary>充电态：电压 → 百分比（充电曲线）。</summary>
        public double PercentFromCharge(double mv)
        {
            if (!HasChargeCurve) return 0;
            return Interpolate(_chargeMv, _chargePct, mv);
        }

        /// <summary>充电态：进入电压平台区后的分钟数 → 百分比（充电时间表）。</summary>
        public double PercentFromSlots(double minutes)
        {
            if (!HasSlots) return 0;
            return Interpolate(_slotMin, _slotPct, minutes);
        }

        /// <summary>线性插值；数组必须是升序。起止段做外推夹取，不外插。</summary>
        private static double Interpolate(double[] xs, double[] ys, double x)
        {
            int n = xs.Length;
            if (x <= xs[0]) return ys[0];
            if (x >= xs[n - 1]) return ys[n - 1];
            for (int i = 1; i < n; i++)
            {
                if (xs[i] >= x)
                {
                    double span = xs[i] - xs[i - 1];
                    if (span <= 0) return ys[i];
                    double t = (x - xs[i - 1]) / span;
                    return ys[i - 1] + (ys[i] - ys[i - 1]) * t;
                }
            }
            return ys[n - 1];
        }
    }
}
