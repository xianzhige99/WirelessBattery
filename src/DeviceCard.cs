using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace WirelessBattery
{
    public class DeviceCard : CardPanel
    {
        private Panel _dot;
        private Label _title;
        private Label _subtitle;
        private Label _linkBadge;
        private Label _badge;
        private Label _level;
        private Label _hint;
        private Label _warn;
        private BatteryBar _bar;
        private Font _hintNormalFont;
        private Font _hintWarnFont;
        private Label _status;
        private Label _detail;
        private LinkLabel _estimateSetup;
        private LinkLabel _gpwSetup;
        private LinkLabel _rename;
        private DeviceCardModel _model;

        /// <summary>卡片上的“阶梯估算”复选框已移除，开关只在“阶梯设置”窗口里。</summary>
        public Action<string, bool> EstimateToggled;
        public Action<DeviceCardModel> EstimateSetupRequested;
        public Action<DeviceCardModel> GpwSetupRequested;
        public Action<DeviceCardModel> RenameRequested;

        public DeviceCard()
        {
            Height = 190;
            Margin = new Padding(0, 0, 0, 12);
            Radius = 12;
            BorderColor = Color.FromArgb(231, 234, 240);

            _dot = new Panel();
            _dot.Size = new Size(10, 10);
            _dot.BackColor = Color.FromArgb(152, 162, 179);
            Controls.Add(_dot);

            _title = NewLabel("", 12.5f, FontStyle.Bold, Ui.Text);
            _title.AutoEllipsis = true;
            Controls.Add(_title);

            _subtitle = NewLabel("", 8.4f, FontStyle.Regular, Ui.Muted);
            _subtitle.AutoEllipsis = true;
            Controls.Add(_subtitle);

            _linkBadge = NewLabel("未连接", 8.2f, FontStyle.Regular, Ui.Muted);
            _linkBadge.TextAlign = ContentAlignment.MiddleCenter;
            _linkBadge.BackColor = Color.FromArgb(242, 244, 247);
            Controls.Add(_linkBadge);

            _badge = NewLabel("", 8.2f, FontStyle.Regular, Ui.Muted);
            _badge.TextAlign = ContentAlignment.MiddleCenter;
            _badge.BackColor = Color.FromArgb(242, 244, 247);
            Controls.Add(_badge);

            _level = NewLabel("—", 31f, FontStyle.Bold, Color.FromArgb(152, 162, 179));
            _level.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(_level);

            _hintNormalFont = Ui.Font(8.8f, FontStyle.Regular);
            _hintWarnFont = Ui.Font(8.8f, FontStyle.Bold);
            _hint = NewLabel("", 8.8f, FontStyle.Regular, Ui.Muted);
            Font initialHintFont = _hint.Font;
            _hint.Font = _hintNormalFont;
            if (initialHintFont != null) initialHintFont.Dispose();
            _hint.AutoEllipsis = true;
            Controls.Add(_hint);

            _warn = NewLabel("", 8.2f, FontStyle.Bold, Color.FromArgb(240, 68, 56));
            _warn.AutoEllipsis = true;
            _warn.Visible = false;
            Controls.Add(_warn);

            _bar = new BatteryBar();
            _bar.Value = 0;
            _bar.BarColor = Ui.Accent;
            Controls.Add(_bar);

            _status = NewLabel("", 8.5f, FontStyle.Regular, Ui.Muted);
            _status.AutoEllipsis = true;
            Controls.Add(_status);

            _detail = NewLabel("", 8.5f, FontStyle.Regular, Ui.Muted);
            _detail.AutoEllipsis = true;
            Controls.Add(_detail);

            // 阶梯估算的开关只在“阶梯设置”窗口里，卡片上不再重复放一个复选框。
            _estimateSetup = new LinkLabel();
            _estimateSetup.Text = "阶梯设置";
            _estimateSetup.Font = Ui.Font(8.2f, FontStyle.Regular);
            _estimateSetup.LinkColor = Ui.Accent;
            _estimateSetup.ActiveLinkColor = Color.FromArgb(67, 56, 202);
            _estimateSetup.AutoSize = true;
            _estimateSetup.Visible = false;
            _estimateSetup.LinkClicked += delegate
            {
                if (_model != null && EstimateSetupRequested != null) EstimateSetupRequested(_model);
            };
            Controls.Add(_estimateSetup);

            _gpwSetup = new LinkLabel();
            _gpwSetup.Text = "电压设置";
            _gpwSetup.Font = Ui.Font(8.2f, FontStyle.Regular);
            _gpwSetup.LinkColor = Ui.Accent;
            _gpwSetup.ActiveLinkColor = Color.FromArgb(67, 56, 202);
            _gpwSetup.AutoSize = true;
            _gpwSetup.Visible = false;
            _gpwSetup.LinkClicked += delegate
            {
                if (_model != null && GpwSetupRequested != null) GpwSetupRequested(_model);
            };
            Controls.Add(_gpwSetup);

            _rename = new LinkLabel();
            _rename.Text = "重命名";
            _rename.Font = Ui.Font(8.2f, FontStyle.Regular);
            _rename.LinkColor = Ui.Accent;
            _rename.ActiveLinkColor = Color.FromArgb(67, 56, 202);
            _rename.AutoSize = true;
            _rename.LinkClicked += delegate
            {
                if (_model != null && RenameRequested != null) RenameRequested(_model);
            };
            Controls.Add(_rename);

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            LayoutChildren();
        }

        private static Label NewLabel(string text, float size, FontStyle style, Color color)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = Ui.Font(size, style);
            l.ForeColor = color;
            l.AutoSize = false;
            l.BackColor = Color.Transparent;
            return l;
        }

        public void SetData(DeviceCardModel m)
        {
            _model = m;
            _title.Text = m.Title;
            _subtitle.Text = m.Subtitle;
            _badge.Text = string.IsNullOrEmpty(m.Mode) || m.Mode == "未连接" ? "—" : m.Mode;
            _linkBadge.Text = m.Connected ? "已连接" : "未连接";
            _linkBadge.ForeColor = m.Connected ? Ui.Text : Ui.Muted;
            _linkBadge.BackColor = Color.FromArgb(242, 244, 247);
            _level.Text = m.LevelText;
            _hint.Text = m.EstimateHint;
            _status.Text = string.IsNullOrEmpty(m.StatusLine) ? m.StatusText : m.StatusLine;
            _detail.Text = m.Detail;

            _warn.Visible = m.EstimateInvalid;
            _warn.Text = m.EstimateInvalid ? "⚠ 阶梯估算设置有误" : "";
            _hint.ForeColor = m.EstimateInvalid ? Color.FromArgb(240, 68, 56) : Ui.Muted;
            _hint.Font = m.EstimateInvalid ? _hintWarnFont : _hintNormalFont;
            _status.ForeColor = m.EstimateInvalid ? Color.FromArgb(240, 68, 56) : Ui.Muted;

            Color border = m.EstimateInvalid ? Color.FromArgb(247, 144, 9) : Color.FromArgb(231, 234, 240);
            if (BorderColor != border)
            {
                BorderColor = border;
                Invalidate();
            }

            Color accent = m.ThemeColor;
            if (m.HasLevel && m.Level.HasValue && m.Level.Value < 30)
                accent = Color.FromArgb(240, 68, 56);
            if (!m.HasLevel)
                accent = Color.FromArgb(152, 162, 179);

            _dot.BackColor = m.Connected ? accent : Color.FromArgb(152, 162, 179);
            _level.ForeColor = accent;
            _bar.BarColor = accent;
            _bar.Value = m.HasLevel && m.Level.HasValue ? m.Level.Value : 0;

            if (!m.Connected)
            {
                _badge.BackColor = Color.FromArgb(242, 244, 247);
                _badge.ForeColor = Color.FromArgb(102, 112, 133);
            }
            else
            {
                _badge.BackColor = BadgeBack(m.Mode);
                _badge.ForeColor = accent;
            }

            _estimateSetup.Visible = m.CanEstimate;
            _gpwSetup.Visible = m.CanGpwSetup;
            _rename.Visible = true;

            // 卡片子控件是独立窗口；父卡片重绘时不一定带上它们。
            // 显式连子控件一起失效，避免第一张卡片偶发只画白底不画文字。
            Invalidate(true);
        }

        private static Color BadgeBack(string mode)
        {
            if (string.IsNullOrEmpty(mode)) return Color.FromArgb(242, 244, 247);
            if (mode.IndexOf("双模", StringComparison.OrdinalIgnoreCase) >= 0) return Color.FromArgb(244, 243, 255);
            if (mode.IndexOf("2.4G", StringComparison.OrdinalIgnoreCase) >= 0) return Color.FromArgb(238, 244, 255);
            if (mode.IndexOf("蓝牙", StringComparison.OrdinalIgnoreCase) >= 0) return Color.FromArgb(232, 247, 255);
            if (mode.IndexOf("连接中", StringComparison.OrdinalIgnoreCase) >= 0) return Color.FromArgb(255, 247, 230);
            if (mode.IndexOf("未连接", StringComparison.OrdinalIgnoreCase) >= 0) return Color.FromArgb(242, 244, 247);
            return Color.FromArgb(244, 243, 255);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutChildren();
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                if (_hintNormalFont != null) { _hintNormalFont.Dispose(); _hintNormalFont = null; }
                if (_hintWarnFont != null) { _hintWarnFont.Dispose(); _hintWarnFont = null; }
            }
        }

        private void LayoutChildren()
        {
            if (_dot == null || _title == null || _subtitle == null || _linkBadge == null || _badge == null ||
                _level == null || _hint == null || _warn == null || _bar == null || _status == null || _detail == null)
                return;
            int pad = 20;
            int w = Width;
            _dot.Location = new Point(pad, 27);
            _badge.Size = new Size(68, 24);
            _badge.Location = new Point(w - pad - 68, 20);
            _linkBadge.Size = new Size(68, 24);
            _linkBadge.Location = new Point(_badge.Left - 8 - 68, 20);
            _title.Location = new Point(pad + 18, 14);
            _title.Size = new Size(w - pad * 2 - 18 - 154, 24);
            _subtitle.Location = new Point(pad + 18, 38);
            _subtitle.Size = new Size(w - pad * 2 - 18 - 154, 18);
            _level.Location = new Point(pad, 62);
            _level.Size = new Size(212, 54);
            _warn.Location = new Point(236, 61);
            _warn.Size = new Size(Math.Max(80, w - 236 - pad), 19);
            _hint.Location = new Point(236, 84);
            _hint.Size = new Size(Math.Max(80, w - 236 - pad), 20);
            _bar.Location = new Point(pad, 124);
            _bar.Size = new Size(w - pad * 2, 12);
            _status.Location = new Point(pad, 144);
            _status.Size = new Size(w - pad * 2, 18);
            _detail.Location = new Point(pad, 164);
            int rightArea = 190;
            _detail.Size = new Size(Math.Max(80, w - pad * 2 - rightArea), 18);
            // 右下角一行链接从右往左排：重命名 → 电压设置 → 阶梯设置。
            int checkY = 163;
            int rightX = w - pad;
            _rename.Location = new Point(rightX - _rename.Width, checkY);
            rightX = _rename.Left - 10;
            _gpwSetup.Location = new Point(rightX - _gpwSetup.Width, checkY);
            if (_gpwSetup.Visible) rightX = _gpwSetup.Left - 10;
            _estimateSetup.Location = new Point(rightX - _estimateSetup.Width, checkY);
            if (_estimateSetup.Visible) rightX = _estimateSetup.Left - 10;
        }
    }
}
