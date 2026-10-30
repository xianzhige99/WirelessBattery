using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace WirelessBattery
{
    /// <summary>
    /// 把设置窗口渲染成 PNG，用于检查布局（不依赖人眼截图）。用法：UiShot.exe [输出路径]
    /// </summary>
    internal static class UiShot
    {
        [STAThread]
        private static int Main(string[] args)
        {
            string outPath = args != null && args.Length > 0
                ? args[0]
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "estimate-settings.png");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            AppSettings settings = new AppSettings();
            EstimateConfig cfg = settings.GetEstimateConfig("K02BS");
            cfg.FullHours = 163;
            settings.SetEstimateConfig("K02BS", cfg);

            using (EstimateSettingsForm form = new EstimateSettingsForm(settings, "K02BS", "西伯利亚 K02BS", delegate { }))
            {
                form.Show();
                Application.DoEvents();
                form.Refresh();
                Application.DoEvents();

                using (Bitmap bmp = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                    bmp.Save(outPath, ImageFormat.Png);
                }

                // 把五个权重改成 1,1,1,1,6（和=10 → 占比 10/10/10/10/60），验证实时刷新链路。
                int index = 0;
                foreach (NumericUpDown box in Collect<NumericUpDown>(form))
                {
                    index++;
                    if (index == 1) continue;              // 第 1 个是“满电使用时间”
                    if (index > 6) break;
                    box.Value = (index == 6) ? 6m : 1m;
                }
                Application.DoEvents();

                string second = Path.Combine(Path.GetDirectoryName(outPath), "estimate-settings-changed.png");
                using (Bitmap bmp = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                    bmp.Save(second, ImageFormat.Png);
                }
                Console.WriteLine("saved: " + second);
            }

            Console.WriteLine("saved: " + outPath);

            // 全局设置窗口
            string settingsShot = Path.Combine(Path.GetDirectoryName(outPath), "settings-form.png");
            using (SettingsForm sf = new SettingsForm(settings, delegate { }))
            {
                sf.Show();
                Application.DoEvents();
                sf.Refresh();
                Application.DoEvents();
                using (Bitmap bmp = new Bitmap(sf.Width, sf.Height))
                {
                    sf.DrawToBitmap(bmp, new Rectangle(0, 0, sf.Width, sf.Height));
                    bmp.Save(settingsShot, ImageFormat.Png);
                }
            }
            Console.WriteLine("saved: " + settingsShot);
            return 0;
        }

        private static List<T> Collect<T>(Control root) where T : Control
        {
            List<T> list = new List<T>();
            for (int i = 0; i < root.Controls.Count; i++)
            {
                Control c = root.Controls[i];
                T hit = c as T;
                if (hit != null) list.Add(hit);
                list.AddRange(Collect<T>(c));
            }
            return list;
        }
    }
}
