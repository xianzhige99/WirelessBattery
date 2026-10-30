using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace WirelessBattery
{
    public class MainForm : Form
    {
        private const int WM_SETREDRAW = 0x000B;
        private const int WM_DEVICECHANGE = 0x0219;
        private const int DBT_DEVNODES_CHANGED = 0x0007;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// 接收设备节点变化消息（插拔/配对）。不枚举窗口、不注册设备接口，
        /// 只清空型号级探测缓存，下一次刷新自然重新探测。
        /// </summary>
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DEVICECHANGE && m.WParam.ToInt32() == DBT_DEVNODES_CHANGED)
            {
                try { _ctx.OnDeviceNodesChanged(); }
                catch { }
            }
            base.WndProc(ref m);
        }

        private readonly TrayContext _ctx;
        private readonly AppSettings _settings;
        private readonly Dictionary<string, DeviceCard> _cardMap = new Dictionary<string, DeviceCard>(StringComparer.OrdinalIgnoreCase);
        private BufferedFlowLayoutPanel _cards;
        private Label _empty;
        private Label _status;
        private Label _subtitle;
        private Button _refreshButton;
        private ScanResult _last;

        public MainForm(TrayContext ctx, AppSettings settings)
        {
            _ctx = ctx;
            _settings = settings;
            Text = "设备电量";
            ClientSize = new Size(800, 600);
            MinimumSize = new Size(760, 560);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(243, 245, 249);
            Font = Ui.Font(9f, FontStyle.Regular);
            DoubleBuffered = true;
            Icon = AppIcon.Shared;
            ShowIcon = true;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96f));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58f));
            root.BackColor = Color.FromArgb(243, 245, 249);
            Controls.Add(root);

            BufferedPanel header = new BufferedPanel();
            header.Dock = DockStyle.Fill;
            header.BackColor = Color.White;
            header.Paint += delegate (object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Ui.Line)) e.Graphics.DrawLine(p, 0, header.Height - 1, header.Width, header.Height - 1);
            };
            root.Controls.Add(header, 0, 0);

            Label title = new Label();
            title.Text = "设备电量";
            title.Font = Ui.Font(20f, FontStyle.Bold);
            title.ForeColor = Ui.Text;
            title.AutoSize = true;
            title.Location = new Point(24, 18);
            header.Controls.Add(title);

            _subtitle = new Label();
            _subtitle.Text = "正在读取设备...";
            _subtitle.Font = Ui.Font(9f, FontStyle.Regular);
            _subtitle.ForeColor = Ui.Muted;
            _subtitle.AutoSize = true;
            _subtitle.Location = new Point(26, 58);
            header.Controls.Add(_subtitle);

            RoundButton refresh = new RoundButton();
            refresh.Text = "立即刷新";
            refresh.Font = Ui.Font(9.5f, FontStyle.Bold);
            refresh.FillColor = Ui.Accent;
            refresh.HoverColor = Color.FromArgb(67, 56, 202);
            refresh.TextColor = Color.White;
            refresh.Size = new Size(112, 38);
            refresh.Click += delegate { _ctx.Refresh(); };
            _refreshButton = refresh;
            header.Controls.Add(_refreshButton);

            header.Resize += delegate { _refreshButton.Left = header.Width - 136; _refreshButton.Top = 20; };
            _refreshButton.Left = 664;
            _refreshButton.Top = 20;

            BufferedPanel center = new BufferedPanel();
            center.Dock = DockStyle.Fill;
            center.BackColor = Color.FromArgb(243, 245, 249);
            root.Controls.Add(center, 0, 1);

            _cards = new BufferedFlowLayoutPanel();
            _cards.Dock = DockStyle.Fill;
            _cards.AutoScroll = true;
            _cards.WrapContents = false;
            _cards.FlowDirection = FlowDirection.TopDown;
            _cards.BackColor = Color.FromArgb(243, 245, 249);
            _cards.Padding = new Padding(18, 16, 18, 16);
            _cards.Resize += delegate { ResizeCards(); };
            center.Controls.Add(_cards);

            BufferedPanel bottom = new BufferedPanel();
            bottom.Dock = DockStyle.Fill;
            bottom.BackColor = Color.White;
            bottom.Paint += delegate (object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Ui.Line)) e.Graphics.DrawLine(p, 0, 0, bottom.Width, 0);
            };
            root.Controls.Add(bottom, 0, 2);

            _status = new Label();
            _status.Text = "正在读取设备...";
            _status.Font = Ui.Font(8.8f, FontStyle.Regular);
            _status.ForeColor = Ui.Muted;
            _status.AutoSize = false;
            _status.AutoEllipsis = true;
            _status.Size = new Size(400, 22);
            _status.Location = new Point(22, 19);
            bottom.Controls.Add(_status);

            Button settingsButton = TextButton("设置");
            settingsButton.Top = 13;
            settingsButton.Click += delegate { _ctx.ShowSettings(); };
            bottom.Controls.Add(settingsButton);

            Button hide = TextButton("隐藏到托盘");
            hide.Top = 13;
            hide.Click += delegate { HideToTray(); };
            bottom.Controls.Add(hide);

            Button exit = TextButton("退出");
            exit.Top = 13;
            exit.Click += delegate { _ctx.Exit(); };
            bottom.Controls.Add(exit);

            // 三个按钮固定 88px 宽、间隔 10px；避免“设置”压到“隐藏到托盘”。
            EventHandler layoutBottom = delegate
            {
                int w = bottom.ClientSize.Width;
                exit.Left = w - 20 - exit.Width;
                hide.Left = exit.Left - 10 - hide.Width;
                settingsButton.Left = hide.Left - 10 - settingsButton.Width;
                _status.Width = Math.Max(160, settingsButton.Left - 34);
            };
            bottom.Resize += layoutBottom;
            layoutBottom(null, EventArgs.Empty);

            FormClosing += delegate (object s, FormClosingEventArgs e)
            {
                if (_ctx.Exiting) return;
                e.Cancel = true;
                if (_settings.CloseToTray)
                {
                    HideToTray();
                }
                else
                {
                    _ctx.Exit();
                }
            };
            FormClosed += delegate { _ctx.OnMainFormClosed(); };
        }

        private static Button TextButton(string text)
        {
            RoundButton b = new RoundButton();
            b.Text = text;
            b.Font = Ui.Font(9f, FontStyle.Regular);
            b.FillColor = Color.White;
            b.HoverColor = Color.FromArgb(242, 244, 247);
            b.TextColor = Ui.Text;
            b.BorderColor = Color.Transparent;
            b.Size = new Size(88, 32);
            return b;
        }

        private void HideToTray()
        {
            if (_ctx.TrayAvailable)
            {
                Hide();
            }
            else
            {
                WindowState = FormWindowState.Minimized;
                ShowInTaskbar = true;
                _ctx.ShowStatus(_ctx.TrayStatusText + "；已最小化到任务栏");
            }
        }

        private static string CardKey(DeviceCardModel m, int index, HashSet<string> used)
        {
            string key = string.IsNullOrEmpty(m.DeviceKey) ? "T:" + (m.Title ?? "") : m.DeviceKey;
            if (used.Contains(key)) key = key + "#" + index;
            return key;
        }

        public void UpdateCards(ScanResult result)
        {
            _last = result;
            if (_cards == null || _cards.IsDisposed) return;

            bool redraw = _cards.IsHandleCreated;
            if (redraw) SendMessage(_cards.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            _cards.SuspendLayout();
            try
            {
                if (result.Cards.Count == 0)
                {
                    foreach (DeviceCard old in _cardMap.Values)
                    {
                        _cards.Controls.Remove(old);
                        old.Dispose();
                    }
                    _cardMap.Clear();

                    if (_empty == null || _empty.IsDisposed) _empty = CreateEmptyLabel();
                    if (!_cards.Controls.Contains(_empty))
                    {
                        _empty.Width = Math.Max(300, _cards.ClientSize.Width - _cards.Padding.Horizontal);
                        _cards.Controls.Add(_empty);
                    }
                }
                else
                {
                    if (_empty != null)
                    {
                        _cards.Controls.Remove(_empty);
                        _empty.Dispose();
                        _empty = null;
                    }

                    HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    List<DeviceCard> ordered = new List<DeviceCard>();
                    for (int i = 0; i < result.Cards.Count; i++)
                    {
                        DeviceCardModel m = result.Cards[i];
                        string key = CardKey(m, i, seen);
                        seen.Add(key);

                        DeviceCard card;
                        if (!_cardMap.TryGetValue(key, out card) || card.IsDisposed)
                        {
                            card = new DeviceCard();
                            card.EstimateToggled += delegate (string k, bool enabled)
                            {
                                _ctx.SetEstimateEnabled(k, enabled);
                            };
                            card.EstimateSetupRequested += delegate (DeviceCardModel model)
                            {
                                _ctx.ShowEstimateSettings(model.DeviceKey, model.Title);
                            };
                            card.GpwSetupRequested += delegate (DeviceCardModel model)
                            {
                                _ctx.ShowGpwSettings(model.DeviceKey, model.Title);
                            };
                            card.RenameRequested += delegate (DeviceCardModel model)
                            {
                                _ctx.ShowDeviceName(model.DeviceKey, model.Title);
                            };
                            _cardMap[key] = card;
                        }
                        if (!_cards.Controls.Contains(card)) _cards.Controls.Add(card);
                        card.SetData(m);
                        ordered.Add(card);
                    }

                    List<string> remove = new List<string>();
                    foreach (KeyValuePair<string, DeviceCard> kv in _cardMap)
                        if (!seen.Contains(kv.Key)) remove.Add(kv.Key);
                    for (int i = 0; i < remove.Count; i++)
                    {
                        DeviceCard old = _cardMap[remove[i]];
                        _cards.Controls.Remove(old);
                        old.Dispose();
                        _cardMap.Remove(remove[i]);
                    }

                    for (int i = 0; i < ordered.Count; i++)
                    {
                        if (_cards.Controls.GetChildIndex(ordered[i]) != i)
                            _cards.Controls.SetChildIndex(ordered[i], i);
                    }
                }
            }
            finally
            {
                ResizeCards();
                if (redraw) SendMessage(_cards.Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
                _cards.ResumeLayout(true);

                // WM_SETREDRAW 关闭期间 SetData 的 Invalidate 会被积压；
                // 恢复后必须显式刷新卡片及其子控件，否则只有托盘更新，
                // 卡片要等窗口最小化/重开才重绘。
                _cards.Invalidate(true);
                _cards.Update();
                foreach (DeviceCard card in _cardMap.Values)
                {
                    if (card != null && !card.IsDisposed)
                    {
                        card.Invalidate(true);
                        card.Update();
                    }
                }
            }

            ComposeStatus();

            StringBuilder sb = new StringBuilder();
            int shown = 0;
            for (int i = 0; i < result.Cards.Count && shown < 3; i++)
            {
                if (!result.Cards[i].Connected) continue;
                if (shown > 0) sb.Append(" · ");
                sb.Append(result.Cards[i].Title);
                shown++;
            }
            _subtitle.Text = sb.Length == 0 ? "暂无已连接设备" : sb.ToString();
        }

        /// <summary>
        /// 状态栏统一在这里拼装：连接数 · 托盘状态 · 单实例/自启审计 · 时间。
        /// 自启审计在后台异步完成，完成时只调 RefreshStatus() 重排这一行，不重扫设备。
        /// </summary>
        private void ComposeStatus()
        {
            if (_status == null || _status.IsDisposed) return;
            int connected = 0;
            DateTime time = DateTime.Now;
            if (_last != null)
            {
                for (int i = 0; i < _last.Cards.Count; i++) if (_last.Cards[i].Connected) connected++;
                time = _last.Time;
            }
            StringBuilder sb = new StringBuilder();
            sb.Append(string.Format("已连接 {0} 台设备 · {1}", connected, _ctx.TrayStatusText));
            string extra = _ctx.StatusExtraText;
            if (!string.IsNullOrEmpty(extra)) sb.Append(" · ").Append(extra);
            sb.Append(" · ").Append(time.ToString("HH:mm:ss"));
            _status.Text = sb.ToString();
        }

        public void RefreshStatus()
        {
            ComposeStatus();
        }

        private Label CreateEmptyLabel()
        {
            Label empty = new Label();
            empty.AutoSize = false;
            empty.Height = 110;
            empty.Text = "没有发现设备。\r\n请确认接收器插好、耳机/鼠标已开机，然后点“立即刷新”。";
            empty.ForeColor = Ui.Muted;
            empty.Font = Ui.Font(10f, FontStyle.Regular);
            empty.Padding = new Padding(6, 12, 0, 0);
            return empty;
        }

        private void ResizeCards()
        {
            if (_cards == null) return;
            int width = _cards.ClientSize.Width - _cards.Padding.Horizontal - 24;
            if (width < 560) width = 560;
            if (_empty != null) _empty.Width = width;
            for (int i = 0; i < _cards.Controls.Count; i++)
            {
                DeviceCard card = _cards.Controls[i] as DeviceCard;
                if (card != null)
                {
                    if (card.Width != width) card.Width = width;
                    if (card.Height != 190) card.Height = 190;
                }
            }
        }

        public void ShowStatus(string text)
        {
            if (_status == null || _status.IsDisposed) return;
            // 一次性提示（例如“已最小化到任务栏”）也不能把单实例/自启审计那一段顶掉。
            string extra = _ctx.StatusExtraText;
            _status.Text = string.IsNullOrEmpty(extra) ? text : text + " · " + extra;
        }

        public void ApplySettings()
        {
            if (Visible) Activate();
        }

        public void AppendLog(string message)
        {
            // 当前 UI 不显示日志，避免额外内存/重绘开销。
        }
    }
}
