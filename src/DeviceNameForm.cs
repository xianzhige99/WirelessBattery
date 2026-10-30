using System;
using System.Drawing;
using System.Windows.Forms;

namespace WirelessBattery
{
    /// <summary>
    /// 设备重命名小窗：只改“显示名”，不参与设备识别（配置键仍是型号级标识）。
    /// 输入为空即表示“用识别到的默认名称”，保存时删除自定义条目。
    /// </summary>
    public class DeviceNameForm : Form
    {
        private readonly AppSettings _settings;
        private readonly string _key;
        private readonly string _defaultTitle;
        private readonly Action _onSaved;

        private TextBox _name;
        private Label _hint;
        private RoundButton _reset;
        private RoundButton _cancel;
        private RoundButton _save;

        public DeviceNameForm(AppSettings settings, string key, string defaultTitle, Action onSaved)
        {
            _settings = settings;
            _key = key;
            _defaultTitle = string.IsNullOrEmpty(defaultTitle) ? AppSettings.DeviceTitle(key) : defaultTitle;
            _onSaved = onSaved;

            Text = "重命名设备";
            ClientSize = new Size(440, 208);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(247, 248, 251);
            Font = Ui.Font(9f, FontStyle.Regular);
            Icon = AppIcon.Shared;

            Label caption = new Label();
            caption.Text = "显示名称";
            caption.AutoSize = true;
            caption.Location = new Point(22, 20);
            caption.ForeColor = Ui.Text;
            Controls.Add(caption);

            _name = new TextBox();
            _name.Location = new Point(22, 46);
            _name.Size = new Size(396, 26);
            _name.MaxLength = ConfigStore.MaxDeviceNameLength;
            _name.Font = Ui.Font(9.5f, FontStyle.Regular);
            _name.Text = settings.GetDeviceName(key);
            Controls.Add(_name);

            _hint = new Label();
            _hint.Location = new Point(22, 82);
            _hint.Size = new Size(396, 52);
            _hint.ForeColor = Ui.Muted;
            _hint.Font = Ui.Font(8.4f, FontStyle.Regular);
            RefreshHint();
            Controls.Add(_hint);

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 58;
            bottom.BackColor = Color.White;
            bottom.Paint += delegate (object s, PaintEventArgs e)
            {
                using (Pen pen = new Pen(Color.FromArgb(233, 236, 242)))
                    e.Graphics.DrawLine(pen, 0, 0, bottom.Width, 0);
            };
            Controls.Add(bottom);

            _reset = new RoundButton();
            _reset.Text = "恢复默认";
            _reset.Size = new Size(96, 32);
            _reset.FillColor = Color.White;
            _reset.TextColor = Ui.Text;
            _reset.BorderColor = Color.FromArgb(214, 220, 231);
            _reset.Click += delegate { _name.Text = ""; RefreshHint(); };
            bottom.Controls.Add(_reset);

            _cancel = new RoundButton();
            _cancel.Text = "取消";
            _cancel.Size = new Size(84, 32);
            _cancel.FillColor = Color.White;
            _cancel.TextColor = Ui.Text;
            _cancel.BorderColor = Color.FromArgb(214, 220, 231);
            _cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            bottom.Controls.Add(_cancel);

            _save = new RoundButton();
            _save.Text = "保存";
            _save.Size = new Size(84, 32);
            _save.FillColor = Ui.Accent;
            _save.TextColor = Color.White;
            _save.Click += delegate { SaveAndClose(); };
            bottom.Controls.Add(_save);

            bottom.Resize += delegate { LayoutBottom(bottom); };
            LayoutBottom(bottom);
        }

        private void RefreshHint()
        {
            if (_hint == null) return;
            string trimmed = _name == null ? "" : _name.Text.Trim();
            _hint.Text = trimmed.Length == 0
                ? "当前使用识别到的默认名称：" + _defaultTitle + "\n输入为空即保持默认名称。"
                : "将显示为：" + ConfigStore.SanitizeName(trimmed) + "\n清空输入框并保存即可恢复默认名称。";
        }

        private void LayoutBottom(Panel bottom)
        {
            int y = (bottom.Height - 32) / 2;
            _reset.Location = new Point(20, y);
            _save.Location = new Point(bottom.Width - 20 - _save.Width, y);
            _cancel.Location = new Point(_save.Left - 10 - _cancel.Width, y);
        }

        private void SaveAndClose()
        {
            _settings.SetDeviceName(_key, _name.Text);
            try { _settings.Save(); } catch (Exception) { }
            if (_onSaved != null) _onSaved();
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
