using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

namespace WirelessBattery
{
    public class EstimateSettingsForm : Form
    {
        private readonly AppSettings _settings;
        private readonly string _key;
        private readonly Action _onSaved;
        private CheckBox _enabled;
        private NumericUpDown _hours;
        private TextBox _stages;
        private Label _current;
        private TextBox[] _weights;
        private Label _weightsPreview;
        private int? _currentLevel;

        public EstimateSettingsForm(AppSettings settings, string key, string title, Action onSaved)
        {
            _settings = settings;
            _key = key;
            _onSaved = onSaved;
            Text = "阶梯电量估算 · " + title;
            ClientSize = new Size(500, 604);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(247, 248, 251);
            Font = Ui.Font(9f, FontStyle.Regular);
            Icon = AppIcon.Shared;

            EstimateConfig cfg = settings.GetEstimateConfig(key);
            RuntimeState rt = settings.GetRuntime(key);
            _currentLevel = rt.HasBtStage ? (int?)rt.BtStage : settings.GetLastLevel(key);

            TableLayoutPanel table = new TableLayoutPanel();
            table.Dock = DockStyle.Fill;
            table.Padding = new Padding(22, 16, 22, 10);
            table.ColumnCount = 2;
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55f));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45f));
            table.RowCount = 9;
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 42f));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 46f));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 104f));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 26f));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 40f));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 26f));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            Controls.Add(table);

            _enabled = new CheckBox();
            _enabled.Text = "启用阶梯电量估算（主数字显示推算电量）";
            _enabled.Checked = cfg.Enabled;
            _enabled.Dock = DockStyle.Fill;
            table.Controls.Add(_enabled, 0, 0);
            table.SetColumnSpan(_enabled, 2);

            table.Controls.Add(RowLabel("满电使用时间（小时）"), 0, 1);
            _hours = new NumericUpDown();
            _hours.Minimum = 1;
            _hours.Maximum = 2000;
            _hours.DecimalPlaces = 1;
            _hours.Increment = 1;
            _hours.Value = (decimal)Math.Max(1.0, Math.Min(2000.0, cfg.FullHours));
            _hours.Dock = DockStyle.Fill;
            _hours.Margin = new Padding(3, 7, 3, 5);
            _hours.ValueChanged += delegate { RefreshWeightsPreview(); };
            table.Controls.Add(_hours, 1, 1);

            Label stageLabel = RowLabel("阶梯数值（从高到低，逗号分隔）");
            table.Controls.Add(stageLabel, 0, 2);
            table.SetColumnSpan(stageLabel, 2);

            _stages = new TextBox();
            _stages.Text = JoinStages(cfg.Stages);
            _stages.Multiline = true;
            _stages.ScrollBars = ScrollBars.Vertical;
            _stages.Dock = DockStyle.Fill;
            _stages.Margin = new Padding(3, 5, 3, 5);
            _stages.Font = new Font("Consolas", 10f);
            _stages.TextChanged += delegate { RefreshCurrent(); };
            table.Controls.Add(_stages, 0, 3);
            table.SetColumnSpan(_stages, 2);

            _current = new Label();
            _current.AutoSize = false;
            _current.Height = 24;
            _current.Dock = DockStyle.Fill;
            _current.Font = Ui.Font(8.5f, FontStyle.Regular);
            _current.Margin = new Padding(4, 3, 4, 2);
            table.Controls.Add(_current, 0, 4);
            table.SetColumnSpan(_current, 2);

            Label weightLabel = RowLabel("电量权重（五个固定区间，相对值，按总和归一化）");
            weightLabel.ForeColor = Ui.Text;
            table.Controls.Add(weightLabel, 0, 5);
            table.SetColumnSpan(weightLabel, 2);

            FlowLayoutPanel weightRow = new FlowLayoutPanel();
            weightRow.Dock = DockStyle.Fill;
            weightRow.Margin = new Padding(3, 2, 3, 4);
            weightRow.WrapContents = false;
            weightRow.AutoScroll = false;
            _weights = new TextBox[5];
            List<double> startWeights = cfg.Weights;
            string startReason;
            if (!AppSettings.IsValidWeights(startWeights, out startReason)) startWeights = EstimateConfig.DefaultWeights();
            for (int i = 0; i < 5; i++)
            {
                Label range = new Label();
                range.Text = string.Format("{0}→{1}", AppSettings.WeightHighs[i], AppSettings.WeightLows[i]);
                range.AutoSize = true;
                range.Margin = new Padding(i == 0 ? 0 : 10, 8, 2, 0);
                range.ForeColor = Ui.Muted;
                range.Font = Ui.Font(8.5f, FontStyle.Regular);
                weightRow.Controls.Add(range);

                TextBox box = new TextBox();
                box.Text = startWeights[i].ToString("0.###", CultureInfo.InvariantCulture);
                box.Width = 52;
                box.Margin = new Padding(0, 5, 0, 0);
                box.TextAlign = HorizontalAlignment.Right;
                box.Font = new Font("Consolas", 9.5f);
                box.TextChanged += delegate { RefreshWeightsPreview(); };
                _weights[i] = box;
                weightRow.Controls.Add(box);
            }
            table.Controls.Add(weightRow, 0, 6);
            table.SetColumnSpan(weightRow, 2);

            _weightsPreview = new Label();
            _weightsPreview.AutoSize = false;
            _weightsPreview.Height = 22;
            _weightsPreview.Dock = DockStyle.Fill;
            _weightsPreview.Margin = new Padding(4, 2, 4, 2);
            _weightsPreview.Font = Ui.Font(8.5f, FontStyle.Regular);
            table.Controls.Add(_weightsPreview, 0, 7);
            table.SetColumnSpan(_weightsPreview, 2);

            Label hint = new Label();
            hint.Text = "规则：蓝牙上报值必须正好等于下面某个阶梯，否则设备卡片会提示“阶梯估算设置有误”。\n" +
                "主数字按五个固定权重区间分配的时间向下一档推算，最低到下一档；小字显示蓝牙反馈值和已保持时间，上报新阶梯后重新计时。";
            hint.Dock = DockStyle.Fill;
            hint.ForeColor = Ui.Muted;
            hint.Font = Ui.Font(8.2f, FontStyle.Regular);
            table.Controls.Add(hint, 0, 8);
            table.SetColumnSpan(hint, 2);
            RefreshCurrent();
            RefreshWeightsPreview();

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
            defaults.Click += delegate
            {
                _stages.Text = "100,80,60,40,20,0";
                _hours.Value = _key.Equals("K02BS", StringComparison.OrdinalIgnoreCase) ? 163 : 100;
                List<double> w = EstimateConfig.DefaultWeights();
                for (int i = 0; i < 5 && i < _weights.Length; i++)
                    _weights[i].Text = w[i].ToString("0.###", CultureInfo.InvariantCulture);
                RefreshCurrent();
                RefreshWeightsPreview();
            };
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
        }

        private static Label RowLabel(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Dock = DockStyle.Fill;
            return l;
        }

        private static string JoinStages(List<int> stages)
        {
            string s = "";
            for (int i = 0; i < stages.Count; i++)
            {
                if (i > 0) s += ",";
                s += stages[i].ToString();
            }
            return s;
        }

        private List<int> ParseStages(bool showError)
        {
            string[] parts = _stages.Text.Split(new char[] { ',', '，', ' ', ';', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            List<int> vals = new List<int>();
            for (int i = 0; i < parts.Length; i++)
            {
                int v;
                if (!int.TryParse(parts[i].Trim(), out v) || v < 0 || v > 100)
                {
                    if (showError)
                        MessageBox.Show(this, "阶梯数值必须是 0~100 的整数：" + parts[i], "格式错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return null;
                }
                if (!vals.Contains(v)) vals.Add(v);
            }
            vals.Sort(delegate (int a, int b) { return b.CompareTo(a); });
            return vals;
        }

        private void RefreshCurrent()
        {
            if (_current == null) return;
            if (!_currentLevel.HasValue)
            {
                _current.Text = "当前蓝牙上报：尚未收到（连接蓝牙后会自动校验）";
                _current.ForeColor = Ui.Muted;
                return;
            }
            List<int> vals = ParseStages(false);
            bool included = vals != null && vals.Contains(_currentLevel.Value);
            _current.Text = included
                ? string.Format("当前蓝牙上报：{0}%（已包含在阶梯中）", _currentLevel.Value)
                : string.Format("当前蓝牙上报：{0}%（不在上面的阶梯中，卡片会提示设置有误）", _currentLevel.Value);
            _current.ForeColor = included ? Color.FromArgb(18, 183, 106) : Color.FromArgb(240, 68, 56);
        }

        private void SaveAndClose()
        {
            List<int> vals = ParseStages(true);
            if (vals == null) return;
            if (vals.Count < 2)
            {
                MessageBox.Show(this, "至少需要 2 个阶梯数值，例如 100,80,60,40,20,0。", "格式错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (_currentLevel.HasValue && !vals.Contains(_currentLevel.Value))
            {
                DialogResult r = MessageBox.Show(this,
                    string.Format("当前蓝牙上报 {0}%，不在新的阶梯列表中。\n保存后设备卡片会提示“阶梯估算设置有误”，直到把 {0} 加进列表。\n\n仍要保存吗？",
                        _currentLevel.Value),
                    "阶梯校验", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) return;
            }

            List<double> weights = ReadWeights();
            string weightReason;
            if (!AppSettings.IsValidWeights(weights, out weightReason))
            {
                MessageBox.Show(this, weightReason + "。\n五个权重都必须是大于 0 的数字，改好后再保存。",
                    "权重设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            EstimateConfig cfg = _settings.GetEstimateConfig(_key);
            cfg.Enabled = _enabled.Checked;
            cfg.FullHours = (double)_hours.Value;
            cfg.Stages = vals;
            cfg.Weights = weights;
            _settings.SetEstimateConfig(_key, cfg);
            _settings.Save();
            if (_onSaved != null) _onSaved();
            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>从五个输入框读权重；任意一项不是数字就返回 null（交给校验去报错）。</summary>
        private List<double> ReadWeights()
        {
            if (_weights == null) return EstimateConfig.DefaultWeights();
            List<double> list = new List<double>();
            for (int i = 0; i < _weights.Length; i++)
            {
                string text = _weights[i].Text == null ? "" : _weights[i].Text.Trim();
                double v;
                bool ok = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
                if (!ok) ok = double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out v);
                if (!ok) return null;
                list.Add(v);
            }
            return list;
        }

        private void RefreshWeightsPreview()
        {
            if (_weightsPreview == null) return;
            List<double> weights = ReadWeights();
            string reason;
            if (!AppSettings.IsValidWeights(weights, out reason))
            {
                _weightsPreview.Text = reason + "（非法权重不会保存）";
                _weightsPreview.ForeColor = Color.FromArgb(240, 68, 56);
                return;
            }
            double[] shares = AppSettings.NormalizedShares(weights);
            double[] hours = AppSettings.IntervalHours(weights, (double)_hours.Value);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 5; i++)
            {
                if (i > 0) sb.Append(" · ");
                sb.Append(Percent(shares[i] * 100));
            }
            StringBuilder hb = new StringBuilder();
            double total = 0;
            for (int i = 0; i < 5; i++)
            {
                if (i > 0) hb.Append(" · ");
                hb.Append(hours[i].ToString("0.#", CultureInfo.InvariantCulture) + "h");
                total += hours[i];
            }
            _weightsPreview.Text = "占比 " + sb.ToString()
                + "　当前 " + _hours.Value.ToString("0.#", CultureInfo.InvariantCulture) + " 小时 → "
                + hb.ToString() + "（合计 " + total.ToString("0.#", CultureInfo.InvariantCulture) + " 小时）";
            _weightsPreview.ForeColor = Ui.Muted;
        }

        private static string Percent(double value)
        {
            return value.ToString("0.#", CultureInfo.InvariantCulture) + "%";
        }
    }
}
