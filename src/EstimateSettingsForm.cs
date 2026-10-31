using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

namespace WirelessBattery
{
    /// <summary>
    /// 阶梯电量估算设置。
    ///
    /// 布局用一张 4 列网格贯穿整页，保证所有内容竖着对齐：
    ///   第 0 列 标签（区间/项目名）    第 1 列 输入框
    ///   第 2 列 占比（右对齐）          第 3 列 预估时长（右对齐，单位放表头）
    /// 「满电使用时间」的输入框与五行权重框同列同宽；占比与时长都是纯数字右对齐，便于逐行比较。
    /// </summary>
    public class EstimateSettingsForm : Form
    {
        private readonly AppSettings _settings;
        private readonly string _key;
        private readonly Action _onSaved;
        private CheckBox _enabled;
        private NumericUpDown _hours;
        private TextBox _stages;
        private Label _current;
        private NumericUpDown[] _weights;
        private Label[] _shareLabels;
        private Label[] _hourLabels;
        private Label _weightSummary;
        private int? _currentLevel;

        private static readonly string[] RangeText = new string[] { "100% → 80%", "80% → 60%", "60% → 40%", "40% → 20%", "20% → 0%" };

        // 列宽固定，整页共用
        private const int ColLabel = 150;
        private const int ColInput = 96;
        private const int ColShare = 74;

        public EstimateSettingsForm(AppSettings settings, string key, string title, Action onSaved)
        {
            _settings = settings;
            _key = key;
            _onSaved = onSaved;
            Text = "阶梯电量估算 · " + title;
            ClientSize = new Size(560, 606);
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
            table.Padding = new Padding(22, 14, 22, 6);
            table.ColumnCount = 4;
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ColLabel));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ColInput));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ColShare));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            table.RowCount = 14;
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 40f));   // 0 启用
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 42f));   // 1 满电时间
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 26f));   // 2 阶梯标签
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 62f));   // 3 阶梯输入
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 26f));   // 4 当前上报
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 26f));   // 5 权重标题
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 22f));   // 6 表头
            for (int i = 0; i < 5; i++)
                table.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));   // 7..11 五行
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 50f));   // 12 汇总
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));   // 13 说明
            Controls.Add(table);

            _enabled = new CheckBox();
            _enabled.Text = "启用阶梯电量估算（主数字显示推算电量）";
            _enabled.Checked = cfg.Enabled;
            _enabled.Dock = DockStyle.Fill;
            _enabled.Margin = new Padding(0, 2, 0, 2);
            table.Controls.Add(_enabled, 0, 0);
            table.SetColumnSpan(_enabled, 4);

            // 与五行权重框同列同宽，竖着对齐
            table.Controls.Add(FieldLabel("满电使用时间"), 0, 1);
            _hours = new NumericUpDown();
            _hours.Minimum = 1;
            _hours.Maximum = 2000;
            _hours.DecimalPlaces = 1;
            _hours.Increment = 1;
            _hours.Value = (decimal)Math.Max(1.0, Math.Min(2000.0, cfg.FullHours));
            _hours.Dock = DockStyle.Fill;
            _hours.Margin = new Padding(0, 5, 0, 5);
            _hours.TextAlign = HorizontalAlignment.Right;
            _hours.Font = new Font("Consolas", 9.5f);
            _hours.ValueChanged += delegate { RefreshWeights(); };
            table.Controls.Add(_hours, 1, 1);
            Label hourUnit = UnitLabel("小时");
            table.Controls.Add(hourUnit, 2, 1);
            table.SetColumnSpan(hourUnit, 2);

            Label stageLabel = FieldLabel("阶梯数值（从高到低，逗号分隔）");
            table.Controls.Add(stageLabel, 0, 2);
            table.SetColumnSpan(stageLabel, 4);

            _stages = new TextBox();
            _stages.Text = JoinStages(cfg.Stages);
            _stages.Multiline = true;
            _stages.ScrollBars = ScrollBars.Vertical;
            _stages.Dock = DockStyle.Fill;
            _stages.Margin = new Padding(0, 2, 0, 4);
            _stages.Font = new Font("Consolas", 10f);
            _stages.TextChanged += delegate { RefreshCurrent(); };
            table.Controls.Add(_stages, 0, 3);
            table.SetColumnSpan(_stages, 4);

            _current = new Label();
            _current.AutoSize = false;
            _current.Dock = DockStyle.Fill;
            _current.TextAlign = ContentAlignment.MiddleLeft;
            _current.Font = Ui.Font(8.5f, FontStyle.Regular);
            _current.Margin = new Padding(0, 0, 0, 4);
            table.Controls.Add(_current, 0, 4);
            table.SetColumnSpan(_current, 4);

            Label weightTitle = FieldLabel("各区间权重（相对值）");
            weightTitle.ForeColor = Ui.Text;
            weightTitle.Font = Ui.Font(9f, FontStyle.Bold);
            table.Controls.Add(weightTitle, 0, 5);
            table.SetColumnSpan(weightTitle, 4);

            table.Controls.Add(HeaderLabel("区间", ContentAlignment.BottomLeft, Padding.Empty), 0, 6);
            table.Controls.Add(HeaderLabel("权重", ContentAlignment.BottomLeft, Padding.Empty), 1, 6);
            table.Controls.Add(HeaderLabel("占比", ContentAlignment.BottomRight, Padding.Empty), 2, 6);
            table.Controls.Add(HeaderLabel("预估时长（小时）", ContentAlignment.BottomRight, new Padding(0, 0, 6, 0)), 3, 6);

            List<double> startWeights = cfg.Weights;
            string startReason;
            if (!AppSettings.IsValidWeights(startWeights, out startReason)) startWeights = EstimateConfig.DefaultWeights();
            _weights = new NumericUpDown[5];
            _shareLabels = new Label[5];
            _hourLabels = new Label[5];

            for (int i = 0; i < 5; i++)
            {
                int row = 7 + i;

                Label range = new Label();
                range.Text = RangeText[i];
                range.TextAlign = ContentAlignment.MiddleLeft;
                range.Dock = DockStyle.Fill;
                range.Margin = new Padding(0);
                range.ForeColor = Ui.Text;
                range.Font = Ui.Font(9f, FontStyle.Regular);
                table.Controls.Add(range, 0, row);

                NumericUpDown box = new NumericUpDown();
                box.Minimum = 0.01m;
                box.Maximum = 10000m;
                box.DecimalPlaces = 2;
                box.Increment = 1m;
                box.Value = (decimal)Math.Max(0.01, Math.Min(10000.0, startWeights[i]));
                box.Dock = DockStyle.Fill;
                box.Margin = new Padding(0, 3, 0, 3);
                box.TextAlign = HorizontalAlignment.Right;
                box.Font = new Font("Consolas", 9.5f);
                box.ValueChanged += delegate { RefreshWeights(); };
                _weights[i] = box;
                table.Controls.Add(box, 1, row);

                Label share = new Label();
                share.TextAlign = ContentAlignment.MiddleRight;
                share.Dock = DockStyle.Fill;
                share.Margin = new Padding(0);
                share.ForeColor = Ui.Text;
                share.Font = new Font("Consolas", 9.5f);
                _shareLabels[i] = share;
                table.Controls.Add(share, 2, row);

                Label hours = new Label();
                hours.TextAlign = ContentAlignment.MiddleRight;
                hours.Dock = DockStyle.Fill;
                hours.Margin = new Padding(0, 0, 6, 0);
                hours.ForeColor = Ui.Muted;
                hours.Font = new Font("Consolas", 9.5f);
                _hourLabels[i] = hours;
                table.Controls.Add(hours, 3, row);
            }

            _weightSummary = new Label();
            _weightSummary.AutoSize = false;
            _weightSummary.Dock = DockStyle.Fill;
            _weightSummary.TextAlign = ContentAlignment.TopLeft;
            _weightSummary.Margin = new Padding(0, 6, 0, 0);
            _weightSummary.Font = Ui.Font(8.4f, FontStyle.Regular);
            _weightSummary.ForeColor = Ui.Muted;
            table.Controls.Add(_weightSummary, 0, 12);
            table.SetColumnSpan(_weightSummary, 4);

            Label hint = new Label();
            hint.Text = "规则：蓝牙上报值必须正好等于上面某个阶梯，否则设备卡片会提示“阶梯估算设置有误”。\n" +
                "占比 = 该区间权重 ÷ 五项之和；每段时长 = 占比 × 满电使用时间，五项加起来正好等于满电时间。\n" +
                "主数字按这些时长向下一档推算，最低到下一档；上报新阶梯后重新计时，只改权重不会重置已保持时间。";
            hint.Dock = DockStyle.Fill;
            hint.ForeColor = Ui.Muted;
            hint.Font = Ui.Font(8.2f, FontStyle.Regular);
            hint.Margin = new Padding(0, 6, 0, 0);
            table.Controls.Add(hint, 0, 13);
            table.SetColumnSpan(hint, 4);

            RefreshCurrent();
            RefreshWeights();

            Controls.Add(BuildBottomBar());
        }

        private static Label FieldLabel(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Dock = DockStyle.Fill;
            l.Margin = new Padding(0);
            return l;
        }

        private static Label UnitLabel(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Dock = DockStyle.Fill;
            l.Margin = new Padding(2, 0, 0, 0);
            l.ForeColor = Ui.Muted;
            l.Font = Ui.Font(8.6f, FontStyle.Regular);
            return l;
        }

        private static Label HeaderLabel(string text, ContentAlignment align, Padding margin)
        {
            Label l = new Label();
            l.Text = text;
            l.TextAlign = align;
            l.Dock = DockStyle.Fill;
            l.Margin = margin;
            l.ForeColor = Ui.Muted;
            l.Font = Ui.Font(8.4f, FontStyle.Regular);
            return l;
        }

        private Panel BuildBottomBar()
        {
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
                    _weights[i].Value = (decimal)w[i];
                RefreshCurrent();
                RefreshWeights();
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
            return bottom;
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

        /// <summary>从五个数字框读权重；NumericUpDown 保证每项都是大于 0 的数字。</summary>
        private List<double> ReadWeights()
        {
            if (_weights == null) return EstimateConfig.DefaultWeights();
            List<double> list = new List<double>();
            for (int i = 0; i < _weights.Length; i++)
            {
                list.Add((double)_weights[i].Value);
            }
            return list;
        }

        /// <summary>
        /// 实时刷新每行「占比 / 预估时长」与底部合计。
        /// 占比 = 该区间权重 ÷ 五项之和；时长 = 占比 × 满电小时。
        /// </summary>
        private void RefreshWeights()
        {
            if (_weights == null || _shareLabels == null || _weightSummary == null) return;
            List<double> weights = ReadWeights();
            string reason;
            if (!AppSettings.IsValidWeights(weights, out reason))
            {
                _weightSummary.Text = reason + "（非法权重不会保存）";
                _weightSummary.ForeColor = Color.FromArgb(240, 68, 56);
                return;
            }

            double sum = 0;
            for (int i = 0; i < weights.Count; i++) sum += weights[i];
            double fullHours = (double)_hours.Value;
            double[] shares = AppSettings.NormalizedShares(weights);
            double[] hours = AppSettings.IntervalHours(weights, fullHours);

            StringBuilder sbShare = new StringBuilder();
            StringBuilder sbHour = new StringBuilder();
            double total = 0;
            for (int i = 0; i < 5; i++)
            {
                _shareLabels[i].Text = Percent(shares[i] * 100);
                _hourLabels[i].Text = hours[i].ToString("0.#", CultureInfo.InvariantCulture);
                if (i > 0) { sbShare.Append(" · "); sbHour.Append(" · "); }
                sbShare.Append(Percent(shares[i] * 100));
                sbHour.Append(hours[i].ToString("0.#", CultureInfo.InvariantCulture));
                total += hours[i];
            }

            _weightSummary.Text =
                "五项之和 " + sum.ToString("0.###", CultureInfo.InvariantCulture) + "　→　占比 " + sbShare.ToString() + "\n"
                + "满电 " + fullHours.ToString("0.#", CultureInfo.InvariantCulture) + " 小时　→　" + sbHour.ToString()
                + " 小时（合计 " + total.ToString("0.#", CultureInfo.InvariantCulture) + " 小时）";
            _weightSummary.ForeColor = Ui.Muted;
        }

        private static string Percent(double value)
        {
            return value.ToString("0.#", CultureInfo.InvariantCulture) + "%";
        }
    }
}
