using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WirelessBattery
{
    internal static class IconFactory
    {
        // 32px 画布坐标系下的字形参数（与样式稿 tools/TrayIconDesign.cs 保持一致）
        private const float H = 26f;            // 字高
        private const float W = 13.6f;          // 字宽
        private const float Spacing = 1.8f;     // 字间距
        private const float T = 3.2f;           // 笔画粗细
        private const float Gap = 1.0f;         // 相邻尖角之间的间隙

        // 七段命名：a=上 b=右上 c=右下 d=下 e=左下 f=左上 g=中
        private static readonly string[] DigitMasks =
        {
            "abcdef", "bc", "abdeg", "abcdg", "bcfg", "acdfg", "acdefg", "abc", "abcdefg", "abcdfg"
        };

        private const string MaskF = "afge";    // 满电 FC：F
        private const string MaskC = "afed";    // 满电 FC：C

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        /// <summary>
        /// 生成托盘图标。level 为 null 时画一条横杠；charging=true 时画两根并排横杠；
        /// level ≥ 99 时画 FC。图标内含 16/20/24/32 四个尺寸，系统按当前 DPI 取最清晰的一档。
        /// </summary>
        public static Icon Create(int? level, Color color, bool charging)
        {
            int[] sizes = { 16, 20, 24, 32 };
            List<byte[]> frames = new List<byte[]>(sizes.Length);
            for (int i = 0; i < sizes.Length; i++)
            {
                using (Bitmap bmp = Render(level, color, charging, sizes[i]))
                    frames.Add(BuildFrame(bmp, sizes[i]));
            }

            using (MemoryStream ico = new MemoryStream())
            {
                BinaryWriter w = new BinaryWriter(ico);
                w.Write((ushort)0);              // reserved
                w.Write((ushort)1);              // type = icon
                w.Write((ushort)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0);            // 调色板数
                    w.Write((byte)0);            // reserved
                    w.Write((ushort)1);          // planes
                    w.Write((ushort)32);         // bpp
                    w.Write(frames[i].Length);
                    w.Write(offset);
                    offset += frames[i].Length;
                }
                for (int i = 0; i < frames.Count; i++) w.Write(frames[i]);
                w.Flush();
                ico.Position = 0;
                // 32bpp DIB 帧，Vista 以上与 GDI+ 都能正确解析
                return new Icon(ico, new Size(32, 32));
            }
        }

        /// <summary>把位图编成 ICO 里的 DIB 帧（BITMAPINFOHEADER + 自下而上 BGRA + 全零 AND 掩码）。</summary>
        private static byte[] BuildFrame(Bitmap bmp, int size)
        {
            int maskStride = ((size + 31) / 32) * 4;
            int xorSize = size * size * 4;
            int andSize = maskStride * size;

            using (MemoryStream ms = new MemoryStream(40 + xorSize + andSize))
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(40);            // biSize
                w.Write(size);          // biWidth
                w.Write(size * 2);      // biHeight（XOR + AND）
                w.Write((short)1);      // biPlanes
                w.Write((short)32);     // biBitCount
                w.Write(0);             // biCompression = BI_RGB
                w.Write(xorSize + andSize);
                w.Write(0); w.Write(0); w.Write(0); w.Write(0);

                for (int y = size - 1; y >= 0; y--)
                    for (int x = 0; x < size; x++)
                    {
                        Color c = bmp.GetPixel(x, y);
                        w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
                    }

                for (int i = 0; i < andSize; i++) w.Write((byte)0);
                w.Flush();
                return ms.ToArray();
            }
        }

        private static Bitmap Render(int? level, Color color, bool charging, int size)
        {
            Bitmap bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);

                float scale = size / 32f;
                using (SolidBrush b = new SolidBrush(color))
                {
                    if (charging) DrawDashes(g, b, scale);
                    else
                    {
                        string text;
                        if (!level.HasValue) text = "dash";
                        else if (level.Value >= 99) text = "FC";
                        else text = level.Value.ToString();
                        DrawText(g, b, text, scale);
                    }
                }
            }
            return bmp;
        }

        private static void DrawText(Graphics g, Brush b, string text, float scale)
        {
            if (text == "dash")
            {
                // 未知电量：只在中间画一条横杠
                float yMid = (32f - H) * 0.5f + (H - T) * 0.5f;
                float cx = 16f;
                HBar(g, b, cx - 5f + Gap * 0.5f, cx + 5f - Gap * 0.5f, yMid + T * 0.5f, T, T * 0.5f);
                return;
            }

            int n = text.Length;
            float totalW = n * W + (n - 1) * Spacing;
            float x0 = (32f - totalW) * 0.5f;
            float y0 = (32f - H) * 0.5f;

            GraphicsState st = g.Save();
            g.ScaleTransform(scale, scale);
            for (int i = 0; i < n; i++)
            {
                string mask = MaskOf(text[i]);
                if (mask == null) continue;
                Digit(g, b, x0 + i * (W + Spacing), y0, W, H, T, Gap, mask);
            }
            g.Restore(st);
        }

        private static void DrawDashes(Graphics g, Brush b, float scale)
        {
            float totalW = 2 * W + Spacing;
            float x0 = (32f - totalW) * 0.5f;
            float y0 = (32f - H) * 0.5f;
            float yMidCy = y0 + (H - T) * 0.5f + T * 0.5f;
            float half = T * 0.5f, hg = Gap * 0.5f;

            GraphicsState st = g.Save();
            g.ScaleTransform(scale, scale);
            HBar(g, b, x0 + half + hg, x0 + W - half - hg, yMidCy, T, half);
            HBar(g, b, x0 + W + Spacing + half + hg, x0 + 2 * W + Spacing - half - hg, yMidCy, T, half);
            g.Restore(st);
        }

        private static string MaskOf(char c)
        {
            if (c >= '0' && c <= '9') return DigitMasks[c - '0'];
            if (c == 'F') return MaskF;
            if (c == 'C') return MaskC;
            return null;
        }

        /// <summary>
        /// 一个字：每段两端都收成 90° 尖角，相邻尖角朝同一交点收拢，只留 Gap 的空隙。
        /// </summary>
        private static void Digit(Graphics g, Brush b, float x, float y, float w, float h, float t, float gap, string mask)
        {
            float half = t * 0.5f;
            float c = half;
            float hg = gap * 0.5f;
            float yMidCy = y + (h - t) * 0.5f + half;
            float aCy = y + half;
            float dCy = y + h - half;
            float lCx = x + half;
            float rCx = x + w - half;

            if (mask.IndexOf('a') >= 0) HBar(g, b, lCx + hg, rCx - hg, aCy, t, c);
            if (mask.IndexOf('g') >= 0) HBar(g, b, lCx + hg, rCx - hg, yMidCy, t, c);
            if (mask.IndexOf('d') >= 0) HBar(g, b, lCx + hg, rCx - hg, dCy, t, c);

            if (mask.IndexOf('f') >= 0) VBar(g, b, lCx, aCy + hg, yMidCy - hg, t, c);
            if (mask.IndexOf('b') >= 0) VBar(g, b, rCx, aCy + hg, yMidCy - hg, t, c);
            if (mask.IndexOf('e') >= 0) VBar(g, b, lCx, yMidCy + hg, dCy - hg, t, c);
            if (mask.IndexOf('c') >= 0) VBar(g, b, rCx, yMidCy + hg, dCy - hg, t, c);
        }

        private static void HBar(Graphics g, Brush b, float x1, float x2, float cy, float t, float c)
        {
            float len = x2 - x1;
            if (len <= 0.2f || t <= 0.2f) return;
            c = Math.Min(c, len * 0.49f);
            float y = cy - t * 0.5f;
            g.FillPolygon(b, new PointF[]
            {
                new PointF(x1 + c, y),
                new PointF(x2 - c, y),
                new PointF(x2, cy),
                new PointF(x2 - c, y + t),
                new PointF(x1 + c, y + t),
                new PointF(x1, cy)
            });
        }

        private static void VBar(Graphics g, Brush b, float cx, float y1, float y2, float t, float c)
        {
            float len = y2 - y1;
            if (len <= 0.2f || t <= 0.2f) return;
            c = Math.Min(c, len * 0.49f);
            float x = cx - t * 0.5f;
            g.FillPolygon(b, new PointF[]
            {
                new PointF(cx, y1),
                new PointF(x + t, y1 + c),
                new PointF(x + t, y2 - c),
                new PointF(cx, y2),
                new PointF(x, y2 - c),
                new PointF(x, y1 + c)
            });
        }

        public static void Destroy(IntPtr hIcon)
        {
            if (hIcon == IntPtr.Zero) return;
            try { DestroyIcon(hIcon); } catch { }
        }
    }

    /// <summary>
    /// 托盘图标配色：鼠标绿、耳机蓝、电量低于 30% 红、充电中黄；
    /// 满电 FC 跟随设备默认色（绿 / 蓝）。
    /// </summary>
    internal static class TrayColors
    {
        public static readonly Color Mouse = Color.FromArgb(34, 197, 94);
        public static readonly Color Headset = Color.FromArgb(59, 130, 246);
        public static readonly Color Low = Color.FromArgb(240, 68, 56);
        public static readonly Color Charging = Color.FromArgb(250, 204, 21);
        public static readonly Color Warning = Color.FromArgb(247, 144, 9);
        public static readonly Color Unknown = Color.FromArgb(152, 162, 179);

        public static bool IsHeadset(DeviceCardModel m)
        {
            string s = ((m.Title ?? "") + " " + (m.Subtitle ?? "")).ToLowerInvariant();
            return s.Contains("耳机") || s.Contains("headset") || s.Contains("headphone") || s.Contains("earphone")
                || s.Contains("xiberia") || s.Contains("西伯利亚") || s.Contains("k02") || s.Contains("k03")
                || s.Contains("jieli") || s.Contains("杰理") || s.Contains("hands-free");
        }

        public static Color For(DeviceCardModel m)
        {
            Color device = IsHeadset(m) ? Headset : Mouse;
            if (m.Charging) return Charging;     // 充电优先：没有百分比时也是黄色
            if (m.EstimateInvalid) return Warning;
            if (m.HasLevel && m.Level.HasValue && m.Level.Value < 30) return Low;
            if (!m.HasLevel) return Unknown;
            return device;                       // 含满电 FC：跟随设备默认色
        }
    }

    internal class TrayItem : IDisposable
    {
        private readonly NotifyIcon _icon;
        private readonly Action _show;
        private readonly Action _refresh;
        private readonly Action _settings;
        private readonly Action _exit;
        private readonly string _key;
        private IntPtr _currentHandle;
        private Icon _image;
        private string _lastText = "";
        private string _lastSig = "";

        public string Key { get { return _key; } }

        public TrayItem(string key, Action show, Action refresh, Action settings, Action exit)
        {
            _key = key;
            _show = show;
            _refresh = refresh;
            _settings = settings;
            _exit = exit;

            _icon = new NotifyIcon();
            _icon.Text = "无线设备电量";
            _image = IconFactory.Create(null, Color.FromArgb(152, 162, 179), false);
            _currentHandle = IntPtr.Zero;
            _icon.Icon = _image;
            _icon.Visible = true;
            _icon.DoubleClick += delegate { _show(); };
            _icon.MouseClick += delegate (object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) _show();
            };
            _icon.ContextMenuStrip = BuildMenu();
        }

        private ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("显示主界面", null, delegate { _show(); });
            menu.Items.Add("立即刷新", null, delegate { _refresh(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("设置", null, delegate { _settings(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { _exit(); });
            return menu;
        }

        public void ShowBalloon(string title, string text)
        {
            try { _icon.ShowBalloonTip(5000, title, text, ToolTipIcon.Info); }
            catch { }
        }

        public void Update(DeviceCardModel m)
        {
            string level = string.IsNullOrEmpty(m.LevelText) ? "" : m.LevelText;
            string text = m.Title + " " + level + (m.IsEstimated ? "（估算）" : "") + " · " + m.Mode;
            if (m.EstimateInvalid) text = m.Title + " " + level + " · 阶梯设置有误";
            if (!m.Connected) text += "（未连接）";
            if (text.Length > 63) text = text.Substring(0, 60) + "...";
            if (text != _lastText)
            {
                _icon.Text = text;
                _lastText = text;
            }

            Color color = TrayColors.For(m);
            bool charging = m.Charging;
            int? iconLevel = m.HasLevel ? m.Level : (int?)null;

            string sig = (iconLevel.HasValue ? iconLevel.Value.ToString() : "-")
                + "|" + color.ToArgb() + "|" + (charging ? 1 : 0) + "|" + m.Connected;
            if (sig != _lastSig)
            {
                Icon next = IconFactory.Create(iconLevel, color, charging);
                Icon old = _image;
                _image = next;
                _icon.Icon = _image;
                if (old != null) old.Dispose();
                _lastSig = sig;
            }
        }

        public void Dispose()
        {
            try
            {
                _icon.Visible = false;
                _icon.Dispose();
            }
            catch { }
            if (_image != null) { _image.Dispose(); _image = null; }
            if (_currentHandle != IntPtr.Zero)
            {
                IconFactory.Destroy(_currentHandle);
                _currentHandle = IntPtr.Zero;
            }
        }
    }

    internal class TrayManager : IDisposable
    {
        private readonly Dictionary<string, TrayItem> _items = new Dictionary<string, TrayItem>(StringComparer.OrdinalIgnoreCase);
        private readonly NotifyIcon _host;
        private readonly Action _show;
        private readonly Action _refresh;
        private readonly Action _settings;
        private readonly Action _exit;
        private IntPtr _hostHandle;
        private Icon _hostImage;
        private string _hostSig = "";
        private string _hostText = "";
        private bool _balloonShown;

        public TrayManager(Action show, Action refresh, Action settings, Action exit)
        {
            _show = show;
            _refresh = refresh;
            _settings = settings;
            _exit = exit;

            _host = new NotifyIcon();
            _host.Icon = AppIcon.Shared;
            _host.Text = "设备电量";
            _host.Visible = true;
            _host.DoubleClick += delegate { _show(); };
            _host.MouseClick += delegate (object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) _show(); };
            _host.ContextMenuStrip = BuildMenu();
        }

        private ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("显示主界面", null, delegate { _show(); });
            menu.Items.Add("立即刷新", null, delegate { _refresh(); });
            menu.Items.Add("设置", null, delegate { _settings(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { _exit(); });
            return menu;
        }

        public void Update(List<DeviceCardModel> cards)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            TrayItem firstNew = null;
            DeviceCardModel primary = null;
            for (int i = 0; i < cards.Count; i++)
            {
                DeviceCardModel m = cards[i];
                if (primary == null && m.Connected && m.HasLevel) primary = m;
                if (!m.Connected && !m.HasLevel) continue;
                string key = string.IsNullOrEmpty(m.DeviceKey) ? m.Title : m.DeviceKey;
                if (seen.Contains(key)) key = key + "#" + i;
                seen.Add(key);
                TrayItem item;
                if (!_items.TryGetValue(key, out item))
                {
                    item = new TrayItem(key, _show, _refresh, _settings, _exit);
                    _items[key] = item;
                    if (firstNew == null) firstNew = item;
                }
                item.Update(m);
            }
            if (primary == null)
            {
                for (int i = 0; i < cards.Count; i++)
                {
                    if (cards[i].HasLevel) { primary = cards[i]; break; }
                }
            }

            List<string> remove = new List<string>();
            foreach (KeyValuePair<string, TrayItem> kv in _items)
                if (!seen.Contains(kv.Key)) remove.Add(kv.Key);
            for (int i = 0; i < remove.Count; i++)
            {
                TrayItem item = _items[remove[i]];
                item.Dispose();
                _items.Remove(remove[i]);
            }

            UpdateHost(primary, cards);
            _host.Visible = true;

            if (!_balloonShown && (firstNew != null || cards.Count > 0))
            {
                _balloonShown = true;
                try
                {
                    _host.ShowBalloonTip(5000, "设备电量已在后台运行",
                        "托盘图标已添加。Windows 11 若把它放进 ^ 溢出区，可点 ^ 展开，右键图标可设置。",
                        ToolTipIcon.Info);
                }
                catch { }
            }
        }

        private void UpdateHost(DeviceCardModel primary, List<DeviceCardModel> cards)
        {
            // 主程序自身的托盘图标固定用软件默认图标，不显示电量数字；
            // 各设备的电量由各自的 TrayItem 图标承担。
            if (_hostSig != "app")
            {
                Icon old = _hostImage;
                _hostImage = null;
                _host.Icon = AppIcon.Shared;
                if (old != null) old.Dispose();
                if (_hostHandle != IntPtr.Zero)
                {
                    IconFactory.Destroy(_hostHandle);
                    _hostHandle = IntPtr.Zero;
                }
                _hostSig = "app";
            }

            string summary = "";
            int shown = 0;
            for (int i = 0; i < cards.Count && shown < 2; i++)
            {
                DeviceCardModel m = cards[i];
                if (!m.HasLevel) continue;
                if (summary.Length > 0) summary += " · ";
                summary += m.Title + " " + m.LevelText;
                shown++;
            }
            string text = summary.Length == 0 ? "设备电量 · 未发现设备" : "设备电量 · " + summary;
            if (text.Length > 63) text = text.Substring(0, 60) + "...";
            if (text != _hostText)
            {
                _host.Text = text;
                _hostText = text;
            }
        }

        public void Dispose()
        {
            foreach (KeyValuePair<string, TrayItem> kv in _items) kv.Value.Dispose();
            _items.Clear();
            try
            {
                _host.Visible = false;
                _host.Dispose();
            }
            catch { }
            if (_hostImage != null) { _hostImage.Dispose(); _hostImage = null; }
            if (_hostHandle != IntPtr.Zero)
            {
                IconFactory.Destroy(_hostHandle);
                _hostHandle = IntPtr.Zero;
            }
        }
    }
}
