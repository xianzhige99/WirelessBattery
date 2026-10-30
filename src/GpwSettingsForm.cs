using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace WirelessBattery
{
    /// <summary>
    /// GPW（罗技 HID++ 电压特征）的换算与滤波参数小窗。
    /// 电压→百分比用内置归一化锂电曲线，本窗口只调整上下限/曲线中段偏移/滤波/防抖参数；
    /// 任一项不合法时拒绝保存且不自动修正。
    /// </summary>
    public class GpwSettingsForm : Form
    {
        private readonly AppSettings _settings;
        private readonly Action _onSaved;

        private TextBox _fullMv;
        private TextBox _emptyMv;
        private TextBox _midOffset;
        private TextBox _window;
        private TextBox _alpha;
        private TextBox _hysteresis;
        private TextBox _rise;
        private CheckBox _autoAdjust;
        private Label _observed;
        private RoundButton _adopt;
        private Label _status;

        private readonly double _originalFullMv;
        private readonly double _originalEmptyMv;

        public GpwSettingsForm(AppSettings settings, string key, string title, Action onSaved)
        {
            _settings = settings;
            _onSaved = onSaved;

            GpwConfig cfg = settings.GetGpwConfig();
            _originalFullMv = cfg.FullMv;
            _originalEmptyMv = cfg.EmptyMv;

            Text = string.IsNullOrEmpty(title) ? "GPW 电压设置" : "GPW 电压设置 · " + title;
            ClientSize = new Size(560, 520);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(247, 248, 251);
            Font = Ui.Font(9f, FontStyle.Regular);
            Icon = AppIcon.Shared;

            TableLayoutPanel table = new TableLayoutPanel();
            table.Dock = DockStyle.Fill;
            table.Padding = new Padding(22, 14, 22, 8);
            table.ColumnCount = 2;
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42f));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58f));
            table.RowCount = 11;
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
            for (int i = 0; i < 7; i++) table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 46f));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 26f));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            Controls.Add(table);

            Label header = new Label();
            header.Text = "电压换算参数（默认按锂电归一化曲线；中段偏移只上下平移曲线中段）";
            header.Dock = DockStyle.Fill;
            header.TextAlign = ContentAlignment.MiddleLeft;
            table.Controls.Add(header, 0, 0);
            table.SetColumnSpan(header, 2);

            _fullMv = AddRow(table, 1, "满电锚点（mV）", cfg.FullMv.ToString("0.###", CultureInfo.InvariantCulture), "3300~4500");
            _emptyMv = AddRow(table, 2, "空电锚点（mV）", cfg.EmptyMv.ToString("0.###", CultureInfo.InvariantCulture), "3000~4500");
            _midOffset = AddRow(table, 3, "中段偏移（%）", cfg.MidOffset.ToString("0.###", CultureInfo.InvariantCulture), "-10 ~ +10");
            _window = AddRow(table, 4, "中值窗口（点）", cfg.MedianWindow.ToString(CultureInfo.InvariantCulture), "1~7");
            _alpha = AddRow(table, 5, "EMA α", cfg.Alpha.ToString("0.###", CultureInfo.InvariantCulture), "0.1~0.5");
            _hysteresis = AddRow(table, 6, "滞回死区（%）", cfg.Hysteresis.ToString("0.###", CultureInfo.InvariantCulture), "变化小于它不刷新显示");
            _rise = AddRow(table, 7, "回升容差（mV）", cfg.RiseTolerance.ToString("0.###", CultureInfo.InvariantCulture), "放电时回升不足它不让数字上涨");

            _autoAdjust = new CheckBox();
            _autoAdjust.Text = "全自动调整满电锚点（只下调，每次 50 mV，至少间隔一个充电周期）";
            _autoAdjust.Font = Ui.Font(8.8f, FontStyle.Regular);
            _autoAdjust.ForeColor = Ui.Text;
            _autoAdjust.BackColor = Color.Transparent;
            _autoAdjust.AutoSize = true;
            _autoAdjust.Checked = cfg.AutoAdjust;
            _autoAdjust.Dock = DockStyle.Fill;
            table.Controls.Add(_autoAdjust, 0, 8);
            table.SetColumnSpan(_autoAdjust, 2);

            Panel observedRow = new Panel();
            observedRow.Dock = DockStyle.Fill;
            table.Controls.Add(observedRow, 0, 9);
            table.SetColumnSpan(observedRow, 2);

            _observed = new Label();
            _observed.Location = new Point(0, 4);
            _observed.Size = new Size(360, 38);
            _observed.TextAlign = ContentAlignment.MiddleLeft;
            _observed.Font = Ui.Font(8.8f, FontStyle.Regular);
            _observed.ForeColor = Ui.Muted;
            observedRow.Controls.Add(_observed);

            _adopt = new RoundButton();
            _adopt.Text = "采用建议值";
            _adopt.Size = new Size(104, 30);
            _adopt.Location = new Point(370, 6);
            _adopt.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _adopt.FillColor = Color.White;
            _adopt.HoverColor = Color.FromArgb(242, 244, 247);
            _adopt.TextColor = Ui.Text;
            _adopt.BorderColor = Ui.Line;
            _adopt.Visible = false;
            _adopt.Click += delegate { AdoptSuggestion(); };
            observedRow.Controls.Add(_adopt);

            _status = new Label();
            _status.Dock = DockStyle.Fill;
            _status.Font = Ui.Font(8.4f, FontStyle.Regular);
            _status.ForeColor = Ui.Muted;
            _status.TextAlign = ContentAlignment.MiddleLeft;
            table.Controls.Add(_status, 0, 10);
            table.SetColumnSpan(_status, 2);

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 58;
            bottom.BackColor = Color.White;
            bottom.Paint += delegate (object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Ui.Line)) e.Graphics.DrawLine(p, 0, 0, bottom.Width, 0);
            };
            RoundButton defaults = new RoundButton();
            defaults.Text = "恢复默认";
            defaults.Size = new Size(90, 34);
            defaults.Location = new Point(16, 12);
            defaults.FillColor = Color.White;
            defaults.HoverColor = Color.FromArgb(242, 244, 247);
            defaults.TextColor = Ui.Text;
            defaults.BorderColor = Ui.Line;
            defaults.Click += delegate { ResetDefaults(); };
            bottom.Controls.Add(defaults);

            RoundButton cancel = new RoundButton();
            cancel.Text = "取消";
            cancel.Size = new Size(88, 34);
            cancel.Location = new Point(bottom.Width - 196, 12);
            cancel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            cancel.FillColor = Color.White;
            cancel.HoverColor = Color.FromArgb(242, 244, 247);
            cancel.TextColor = Ui.Text;
            cancel.BorderColor = Ui.Line;
            cancel.Click += delegate { Close(); };
            bottom.Controls.Add(cancel);

            RoundButton save = new RoundButton();
            save.Text = "保存";
            save.Size = new Size(88, 34);
            save.Location = new Point(bottom.Width - 100, 12);
            save.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            save.FillColor = Ui.Accent;
            save.HoverColor = Color.FromArgb(67, 56, 202);
            save.TextColor = Color.White;
            save.Click += delegate { SaveAndClose(); };
            bottom.Controls.Add(save);
            bottom.Resize += delegate
            {
                cancel.Left = bottom.Width - 196;
                save.Left = bottom.Width - 100;
            };
            Controls.Add(bottom);

            RefreshObserved();
        }

        private TextBox AddRow(TableLayoutPanel table, int row, string label, string value, string note)
        {
            Label l = new Label();
            l.Text = label;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Dock = DockStyle.Fill;
            table.Controls.Add(l, 0, row);

            Panel host = new Panel();
            host.Dock = DockStyle.Fill;
            table.Controls.Add(host, 1, row);

            TextBox box = new TextBox();
            box.Text = value;
            box.TextAlign = HorizontalAlignment.Right;
            box.Font = new Font("Consolas", 10f);
            box.Location = new Point(0, 4);
            box.Size = new Size(110, 24);
            host.Controls.Add(box);

            Label hint = new Label();
            hint.Text = note;
            hint.AutoSize = true;
            hint.ForeColor = Ui.Muted;
            hint.Font = Ui.Font(8.2f, FontStyle.Regular);
            hint.Location = new Point(120, 8);
            host.Controls.Add(hint);
            return box;
        }

        /// <summary>把观测最高非充电电压、样本数与建议锚点画到只读行上。</summary>
        private void RefreshObserved()
        {
            GpwConfig cfg = _settings.GetGpwConfig();
            int suggest = GpwBattery.SuggestFullMv(cfg);
            if (cfg.Samples <= 0)
            {
                _observed.Text = "观测最高非充电电压：尚无样本（需在非充电、拔线 5 分钟后读数）";
            }
            else
            {
                _observed.Text = string.Format("观测最高非充电电压 {0:0} mV · 样本 {1}{2}",
                    cfg.ObservedMaxMv, cfg.Samples,
                    suggest > 0 ? "\n建议把满电锚点调整为 " + suggest.ToString(CultureInfo.InvariantCulture) + " mV" : "");
            }
            _adopt.Visible = suggest > 0;
            _status.Text = cfg.AutoAdjust
                ? "自动调整已开启：只下调、每次 50 mV、下限 4000 mV；手动改过锚点后停止。"
                : "自动调整已关闭：满电锚点只在点击“保存”或“采用建议值”时改变。";
            _status.ForeColor = Ui.Muted;
        }

        private double? ReadNumber(TextBox box)
        {
            string text = box.Text.Trim();
            double v;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v)
                && !double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out v))
                return null;
            return v;
        }

        /// <summary>把当前输入框读成一份配置（保留未在界面上编辑的运行时字段）。</summary>
        private GpwConfig ReadConfig(out string error)
        {
            error = "";
            GpwConfig cfg = _settings.GetGpwConfig().Clone();
            double? full = ReadNumber(_fullMv);
            double? empty = ReadNumber(_emptyMv);
            double? mid = ReadNumber(_midOffset);
            double? win = ReadNumber(_window);
            double? alpha = ReadNumber(_alpha);
            double? hyst = ReadNumber(_hysteresis);
            double? rise = ReadNumber(_rise);
            if (!full.HasValue || !empty.HasValue || !mid.HasValue || !win.HasValue || !alpha.HasValue
                || !hyst.HasValue || !rise.HasValue)
            {
                error = "所有输入框都必须是数字。";
                return null;
            }
            cfg.FullMv = full.Value;
            cfg.EmptyMv = empty.Value;
            cfg.MidOffset = mid.Value;
            cfg.MedianWindow = (int)Math.Round(win.Value);
            cfg.Alpha = alpha.Value;
            cfg.Hysteresis = hyst.Value;
            cfg.RiseTolerance = rise.Value;
            cfg.AutoAdjust = _autoAdjust.Checked;

            string reason;
            if (!AppSettings.IsValidGpw(cfg, out reason))
            {
                error = reason + "。";
                return null;
            }
            return cfg;
        }

        private void ResetDefaults()
        {
            GpwConfig d = new GpwConfig();
            _fullMv.Text = d.FullMv.ToString("0.###", CultureInfo.InvariantCulture);
            _emptyMv.Text = d.EmptyMv.ToString("0.###", CultureInfo.InvariantCulture);
            _midOffset.Text = d.MidOffset.ToString("0.###", CultureInfo.InvariantCulture);
            _window.Text = d.MedianWindow.ToString(CultureInfo.InvariantCulture);
            _alpha.Text = d.Alpha.ToString("0.###", CultureInfo.InvariantCulture);
            _hysteresis.Text = d.Hysteresis.ToString("0.###", CultureInfo.InvariantCulture);
            _rise.Text = d.RiseTolerance.ToString("0.###", CultureInfo.InvariantCulture);
            _autoAdjust.Checked = d.AutoAdjust;
        }

        private void AdoptSuggestion()
        {
            GpwConfig cfg = _settings.GetGpwConfig();
            int suggest = GpwBattery.SuggestFullMv(cfg);
            if (suggest <= 0) return;

            DialogResult answer = MessageBox.Show(this,
                string.Format("把满电锚点从 {0:0} mV 调整为 {1} mV 并保存？\n观测最高非充电电压 {2:0} mV，样本 {3} 次。",
                    cfg.FullMv, suggest, cfg.ObservedMaxMv, cfg.Samples),
                "采用建议锚点", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;

            string error;
            GpwConfig edited = ReadConfig(out error);
            if (edited == null)
            {
                MessageBox.Show(this, error, "参数无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            edited.FullMv = suggest;
            edited.ManualEdited = true;
            string reason;
            if (!_settings.SetGpwConfig(edited, out reason))
            {
                MessageBox.Show(this, reason + "。\n请修改后再保存（不会自动修正）。", "参数无效",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _fullMv.Text = suggest.ToString("0.###", CultureInfo.InvariantCulture);
            if (_onSaved != null) _onSaved();
            RefreshObserved();
        }

        private void SaveAndClose()
        {
            string error;
            GpwConfig cfg = ReadConfig(out error);
            if (cfg == null)
            {
                MessageBox.Show(this, error + "\n请修改后再保存（不会自动修正）。", "参数无效",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            // 只有手动改过锚点才置 ManualEdited，避免调一下滤波参数就把自动调整永久关掉。
            // 直接用不等比较：值要么来自文本框解析，要么来自原配置，不需要浮点容差。
            if (cfg.FullMv != _originalFullMv || cfg.EmptyMv != _originalEmptyMv)
                cfg.ManualEdited = true;

            string reason;
            if (!_settings.SetGpwConfig(cfg, out reason))
            {
                MessageBox.Show(this, reason + "。\n请修改后再保存（不会自动修正）。", "参数无效",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (_onSaved != null) _onSaved();
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
