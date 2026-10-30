using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WirelessBattery
{
    internal static class IconFactory
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public static Icon Create(int? level, Color color, out IntPtr hIcon)
        {
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);
                using (GraphicsPath path = Ui.Round(new Rectangle(2, 2, 28, 28), 8))
                using (SolidBrush b = new SolidBrush(color))
                    g.FillPath(b, path);

                string text = level.HasValue ? level.Value.ToString() : "—";
                float size = text.Length >= 3 ? 9.5f : (text.Length == 2 ? 12.5f : 15f);
                using (Font f = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Point))
                using (SolidBrush b = new SolidBrush(Color.White))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(text, f, b, new RectangleF(0, 0, 32, 33), sf);
                }
            }

            hIcon = bmp.GetHicon();
            bmp.Dispose();
            return Icon.FromHandle(hIcon);
        }

        public static void Destroy(IntPtr hIcon)
        {
            if (hIcon == IntPtr.Zero) return;
            try { DestroyIcon(hIcon); } catch { }
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
            IntPtr h;
            _image = IconFactory.Create(null, Color.FromArgb(152, 162, 179), out h);
            _currentHandle = h;
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

            Color color = Color.FromArgb(152, 162, 179);
            if (m.HasLevel)
            {
                color = m.ThemeColor;
                if (m.Level.HasValue && m.Level.Value < 30) color = Color.FromArgb(240, 68, 56);
            }
            if (m.EstimateInvalid) color = Color.FromArgb(247, 144, 9);

            int? iconLevel = m.HasLevel ? m.Level : (int?)null;
            string sig = (iconLevel.HasValue ? iconLevel.Value.ToString() : "-") + "|" + color.ToArgb() + "|" + m.Connected;
            if (sig != _lastSig)
            {
                IntPtr h;
                Icon next = IconFactory.Create(iconLevel, color, out h);
                IntPtr oldHandle = _currentHandle;
                Icon old = _image;
                _image = next;
                _currentHandle = h;
                _icon.Icon = _image;
                if (old != null) old.Dispose();
                // 系统在 NIM_MODIFY 时已复制图标，旧句柄换下来即可销毁。
                // 只在退出时统一销毁会让长跑进程持续累积 GDI 句柄（阶梯推算值每轮都在变）。
                if (oldHandle != IntPtr.Zero) IconFactory.Destroy(oldHandle);
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
            int? level = null;
            Color color = Color.FromArgb(152, 162, 179);
            string sig = "app";
            if (primary != null && primary.HasLevel)
            {
                level = primary.Level;
                color = primary.ThemeColor;
                if (primary.Level.HasValue && primary.Level.Value < 30) color = Color.FromArgb(240, 68, 56);
                if (primary.EstimateInvalid) color = Color.FromArgb(247, 144, 9);
                sig = "num|" + (level.HasValue ? level.Value.ToString() : "-") + "|" + color.ToArgb();
            }

            if (sig != _hostSig)
            {
                if (level.HasValue)
                {
                    IntPtr h;
                    Icon next = IconFactory.Create(level, color, out h);
                    IntPtr oldHandle = _hostHandle;
                    Icon old = _hostImage;
                    _hostImage = next;
                    _hostHandle = h;
                    _host.Icon = _hostImage;
                    if (old != null) old.Dispose();
                    if (oldHandle != IntPtr.Zero) IconFactory.Destroy(oldHandle);
                }
                else
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
                }
                _hostSig = sig;
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
