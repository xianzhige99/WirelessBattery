using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace WirelessBattery
{
    public class SettingsForm : Form
    {
        private readonly AppSettings _settings;
        private readonly Action _onSaved;
        private CheckBox _autoStart;
        private CheckBox _closeToTray;
        private CheckBox _k02bsSoleDongle;
        private ComboBox _interval;
        private CheckedListBox _devices;
        private readonly List<string> _deviceKeys = new List<string>();

        public SettingsForm(AppSettings settings, Action onSaved)
        {
            _settings = settings;
            _onSaved = onSaved;
            Text = "设置";
            ClientSize = new Size(520, 500);
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
            table.Padding = new Padding(20, 16, 20, 12);
            table.ColumnCount = 2;
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58f));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42f));
            table.RowCount = 8;
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 42f));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 52f));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 40f));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 26f));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 64f));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            Controls.Add(table);

            Label general = Header("常规");
            table.Controls.Add(general, 0, 0);
            table.SetColumnSpan(general, 2);

            _autoStart = new CheckBox();
            _autoStart.Text = "开机自动启动（登录后隐藏到托盘）";
            _autoStart.Checked = _settings.AutoStartWanted;
            _autoStart.Dock = DockStyle.Fill;
            _autoStart.Margin = new Padding(2, 4, 2, 4);
            table.Controls.Add(_autoStart, 0, 1);
            table.SetColumnSpan(_autoStart, 2);

            _closeToTray = new CheckBox();
            _closeToTray.Text = "关闭窗口时隐藏到托盘（不退出程序）";
            _closeToTray.Checked = _settings.CloseToTray;
            _closeToTray.Dock = DockStyle.Fill;
            _closeToTray.Margin = new Padding(2, 4, 2, 4);
            table.Controls.Add(_closeToTray, 0, 2);
            table.SetColumnSpan(_closeToTray, 2);

            _k02bsSoleDongle = new CheckBox();
            _k02bsSoleDongle.Text = "K02BS 只插接收器、耳机未开机时，也显示为已连接（较宽松）";
            _k02bsSoleDongle.Checked = _settings.K02BSSoleDongleAsConnected;
            _k02bsSoleDongle.Dock = DockStyle.Fill;
            _k02bsSoleDongle.Margin = new Padding(2, 2, 2, 4);
            _k02bsSoleDongle.Font = Ui.Font(8.8f, FontStyle.Regular);
            table.Controls.Add(_k02bsSoleDongle, 0, 3);
            table.SetColumnSpan(_k02bsSoleDongle, 2);

            Label refreshLabel = new Label();
            refreshLabel.Text = "自动刷新间隔";
            refreshLabel.TextAlign = ContentAlignment.MiddleLeft;
            refreshLabel.Dock = DockStyle.Fill;
            table.Controls.Add(refreshLabel, 0, 4);

            _interval = new ComboBox();
            _interval.DropDownStyle = ComboBoxStyle.DropDownList;
            _interval.Items.AddRange(new object[] { "30 秒", "1 分钟", "5 分钟", "10 分钟" });
            _interval.Dock = DockStyle.Fill;
            _interval.Margin = new Padding(3, 7, 3, 5);
            if (_settings.IntervalSeconds <= 30) _interval.SelectedIndex = 0;
            else if (_settings.IntervalSeconds <= 60) _interval.SelectedIndex = 1;
            else if (_settings.IntervalSeconds <= 300) _interval.SelectedIndex = 2;
            else _interval.SelectedIndex = 3;
            table.Controls.Add(_interval, 1, 4);

            Label deviceLabel = new Label();
            deviceLabel.Text = "设备（勾选后才会读取电量并显示卡片）";
            deviceLabel.Dock = DockStyle.Fill;
            deviceLabel.TextAlign = ContentAlignment.BottomLeft;
            table.Controls.Add(deviceLabel, 0, 5);
            table.SetColumnSpan(deviceLabel, 2);

            _devices = new CheckedListBox();
            _devices.CheckOnClick = true;
            _devices.BorderStyle = BorderStyle.FixedSingle;
            _devices.IntegralHeight = false;
            _devices.Dock = DockStyle.Fill;
            _devices.Font = Ui.Font(9f, FontStyle.Regular);
            _devices.Margin = new Padding(3, 3, 3, 5);
            // 列表按“上一次扫描发现的型号”动态生成（MCHOSE、K02BS、046D:C539、BT:<名称>…），
            // 新型号默认勾选；勾选状态就是门控开关：不勾选就完全不请求该设备。
            List<string> keys = _settings.GetDeviceKeys();
            for (int i = 0; i < keys.Count; i++)
            {
                string key = keys[i];
                _deviceKeys.Add(key);
                _devices.Items.Add(_settings.TitleOf(key), _settings.IsDeviceEnabled(key));
            }
            table.Controls.Add(_devices, 0, 6);
            table.SetColumnSpan(_devices, 2);

            Label hint = new Label();
            hint.Text = "勾选后才会向该设备请求电量并显示卡片与托盘图标；不勾选则完全不请求、不显示，蓝牙阶梯计时暂停，重新勾选从暂停处继续。\n未勾选的型号仍留在上面这个列表里，随时可以重新勾选。\n蓝牙阶梯估算在对应设备卡片右下角设置：可开关、改满电时间、增删阶梯。\n开机自启优先使用“任务计划程序”：登录后隐藏启动，不经过资源管理器。";
            hint.Dock = DockStyle.Fill;
            hint.ForeColor = Ui.Muted;
            hint.Font = Ui.Font(8.4f, FontStyle.Regular);
            hint.Margin = new Padding(4, 8, 4, 4);
            table.Controls.Add(hint, 0, 7);
            table.SetColumnSpan(hint, 2);

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 58;
            bottom.BackColor = Color.White;
            bottom.Paint += delegate (object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Ui.Line)) e.Graphics.DrawLine(p, 0, 0, bottom.Width, 0);
            };

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

        private static Label Header(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = Ui.Font(11.5f, FontStyle.Bold);
            l.ForeColor = Ui.Text;
            l.TextAlign = ContentAlignment.BottomLeft;
            l.Dock = DockStyle.Fill;
            return l;
        }

        private void SaveAndClose()
        {
            int interval = 60;
            switch (_interval.SelectedIndex)
            {
                case 0: interval = 30; break;
                case 1: interval = 60; break;
                case 2: interval = 300; break;
                case 3: interval = 600; break;
            }
            _settings.IntervalSeconds = interval;
            _settings.CloseToTray = _closeToTray.Checked;
            _settings.K02BSSoleDongleAsConnected = _k02bsSoleDongle.Checked;
            for (int i = 0; i < _deviceKeys.Count && i < _devices.Items.Count; i++)
                _settings.EnabledDevices[_deviceKeys[i]] = _devices.GetItemChecked(i);
            bool ok = _settings.ApplyAutoStart(_autoStart.Checked);
            if (!ok)
            {
                MessageBox.Show(this,
                    "开机自启设置失败：计划任务、启动文件夹和注册表都没有写入成功。\n" +
                    "请手动把 WirelessBattery.exe 的快捷方式放进“启动”文件夹（Win+R 输入 shell:startup）。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            _settings.Save();
            if (_onSaved != null) _onSaved();
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
