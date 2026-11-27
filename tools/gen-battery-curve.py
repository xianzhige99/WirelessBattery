#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
从罗技 G HUB 网页版的数据仓库生成 src/LogitechBatteryData.cs。

数据源（与 G HUB 网页版同源，公开可访问）：
  /depots/devices.json                  PID -> 型号目录 manifest
  <manifest>                            extra[key=battery].src -> battery.xml
  <battery.xml>                         discharge / charging / charging_time_slots

放电表原始是 (时间, 电压, 累计放电 mWh)，按总能量归一：
  SoC = (1 - 累计mWh / 总mWh) * 100
生成阶段就按电压插值算好 SoC（编码为 0..1000 的整数），运行时只做一次分段线性插值。

输出编码：
  Discharge  "mV:SoC*10;..."（电压升序）
  Charge     "mV:%;..."
  Slots      "min:%;..."

用法： py -3 tools/gen-battery-curve.py
"""
import json
import math
import os
import re
import sys
import urllib.request

BASE = "https://ghub.logitechg.com.cn"
# 只收录带放电曲线的型号；顺序即生成顺序
TARGET_PIDS = [
    ("4079", "PRO Wireless"),
    ("407f", "G502 LIGHTSPEED"),
    ("4086", "G703 LIGHTSPEED"),
    ("4067", "G903"),            # 旧文档记作 4053，G HUB 现用 4067
    ("4087", "G903 LIGHTSPEED"),
    ("4070", "G703"),
]
MAX_ERR = 0.4          # 抽稀允许的最大 SoC 误差（百分点）


def get(path):
    url = path if path.startswith("http") else BASE + path
    req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
    with urllib.request.urlopen(req, timeout=30) as r:
        return r.read().decode("utf-8", "replace")


def cdata(xml, tag):
    m = re.search(r"<%s[^>]*>\s*<!\[CDATA\[(.*?)\]\]>" % tag, xml, re.S)
    return m.group(1).strip() if m else None


def attr(xml, tag, name, default=None):
    m = re.search(r"<%s[^>]*\b%s=\"([^\"]+)\"" % (tag, name), xml)
    return m.group(1) if m else default


def rows_of(text, cols):
    out = []
    if not text:
        return out
    for line in text.splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        parts = [p.strip() for p in line.split(",")]
        if len(parts) < cols:
            continue
        try:
            out.append(tuple(float(p) for p in parts[:cols]))
        except ValueError:
            continue
    return out


def soc_at(points, mv, total):
    """points: [(mv, mwh)] 按电压升序；返回剩余电量百分比。"""
    if mv <= points[0][0]:
        return 0.0        # 低压端 = 空
    if mv >= points[-1][0]:
        return 100.0
    for i in range(1, len(points)):
        a, b = points[i - 1], points[i]
        if b[0] >= mv:
            t = (mv - a[0]) / (b[0] - a[0])
            e = a[1] + (b[1] - a[1]) * t
            return 100.0 * (1.0 - e / total)
    return 0.0


def decimate(points, total):
    """按 5mV 网格上的最大误差挑一个最稀但仍满足 MAX_ERR 的步长。"""
    step_limit = max(2, int((points[-1][0] - points[0][0])))
    for stride in (20, 16, 12, 10, 8, 6, 5, 4, 3, 2, 1):
        sel = points[::stride]
        if sel[-1] != points[-1]:
            sel.append(points[-1])
        err = 0.0
        mv = math.ceil(points[0][0])
        while mv <= points[-1][0]:
            err = max(err, abs(soc_at(points, mv, total) - soc_at(sel, mv, total)))
            if err > MAX_ERR:
                break
            mv += 5
        if err <= MAX_ERR:
            return sel, err, stride
    return points, 0.0, 1


def build(pid_hex, fallback_name):
    raw = json.loads(get("/depots/devices.json"))
    devices = {}
    for k, v in raw.items():
        devices[k.lstrip("/").lower()] = v
    path = devices.get(pid_hex.lower())
    if not path:
        return None
    manifest = json.loads(get(path))
    battery = None
    for e in manifest.get("extra", []):
        if e.get("key") == "battery":
            battery = e.get("src")
            break
    if not battery:
        return None
    xml = get(battery)

    name = manifest.get("name") or fallback_name
    stable = float(attr(xml, "charging_time_slots", "stable_state_detection_voltage", 0) or 0)
    gap = float(attr(xml, "charging_time_slots", "gap_fill_speed", 0) or 0)

    dis_raw = rows_of(cdata(xml, "discharge"), 3)
    if len(dis_raw) < 4:
        return None
    total = dis_raw[-1][2]
    # 原始表是放电从满到空（电压递减），反转成升序
    pts = [(v * 1000.0, e) for (_t, v, e) in dis_raw][::-1]
    sel, err, stride = decimate(pts, total)

    discharge = ";".join("%d:%d" % (round(mv), round(soc_at(pts, mv, total) * 10)) for mv, _e in sel)

    charge = ""
    ch_raw = rows_of(cdata(xml, "charging"), 2)
    if len(ch_raw) >= 2:
        charge = ";".join("%.0f:%.0f" % (v * 1000.0, p) for v, p in ch_raw)

    slots = ""
    sl_raw = rows_of(cdata(xml, "charging_time_slots"), 2)
    if len(sl_raw) >= 2:
        slots = ";".join("%.0f:%.0f" % (m, p) for m, p in sl_raw)

    return dict(pid=int(pid_hex, 16), name=name, discharge=discharge, charge=charge,
                slots=slots, stable=stable, gap=gap, err=err, stride=stride,
                points=len(sel), raw=len(pts))


def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    models = []
    for pid_hex, name in TARGET_PIDS:
        try:
            m = build(pid_hex, name)
        except Exception as e:                     # 网络或格式问题：跳过并提示
            print("skip %s: %s" % (pid_hex, e))
            continue
        if not m:
            print("skip %s: 无放电曲线" % pid_hex)
            continue
        models.append(m)
        print("%s %-18s 原始 %4d 点 -> %4d 点(步长 %2d, 误差 %.2f)  充电 %d 段  时间表 %d 段  阈值 %.0f  补速 %.3f"
              % (pid_hex, m["name"], m["raw"], m["points"], m["stride"], m["err"],
                 m["charge"].count(";") + 1 if m["charge"] else 0,
                 m["slots"].count(";") + 1 if m["slots"] else 0, m["stable"], m["gap"]))

    if len(models) < 3:
        print("有效型号过少，放弃生成")
        return 1

    lines = []
    a = lines.append
    a("// 本文件由 tools/gen-battery-curve.py 自动生成，请勿手改。")
    a("// 数据源：罗技 G HUB 网页版 /depots/<model>/battery.xml（与网页版同一份曲线）。")
    a("// Discharge 为 \"电压mV:剩余电量x10;...\"（电压升序，运行时只做一次线性插值）。")
    a("using System;")
    a("")
    a("namespace WirelessBattery")
    a("{")
    a("    internal static class LogitechBatteryData")
    a("    {")
    a("        internal sealed class Model")
    a("        {")
    a("            public int Pid;")
    a("            public string Name;")
    a("            public string Discharge;   // mV : SoCx10")
    a("            public string Charge;      // mV : %")
    a("            public string Slots;       // min : %")
    a("            public double StableMv;    // 充电平台区判定电压，0 = 无该字段")
    a("            public double GapFill;     // 每 30 秒最多补多少个百分点")
    a("        }")
    a("")
    a("        internal static readonly Model[] Models = new Model[]")
    a("        {")
    for m in models:
        a('            new Model { Pid = 0x%04X, Name = "%s",' % (m["pid"], m["name"]))
        a('                Discharge = "%s",' % m["discharge"])
        a('                Charge = "%s",' % m["charge"])
        a('                Slots = "%s",' % m["slots"])
        a('                StableMv = %s, GapFill = %s },' % (
            ("%.0f" % m["stable"]), ("%g" % m["gap"])))
    a("        };")
    a("    }")
    a("}")
    out = os.path.join(root, "src", "LogitechBatteryData.cs")
    with open(out, "w", encoding="utf-8", newline="\r\n") as f:
        f.write("\n".join(lines) + "\n")
    print("\n已写入 %s（%d 个型号，%.1f KB）" % (out, len(models), os.path.getsize(out) / 1024.0))
    return 0


if __name__ == "__main__":
    sys.exit(main())
