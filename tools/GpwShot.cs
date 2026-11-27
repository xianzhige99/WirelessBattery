using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace WirelessBattery
{
    /// <summary>
    /// 把 GPW 设置窗口按「设备曲线 / 通用曲线」两种模式各渲染一张 PNG，用于检查布局。
    /// 只构造窗口、不保存配置；在临时目录里运行，避免碰到真实的 settings.ini。
    /// </summary>
    internal static class GpwShot
    {
        [STAThread]
        private static int Main(string[] args)
        {
            string dir = args != null && args.Length > 0 ? args[0] : AppDomain.CurrentDomain.BaseDirectory;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            AppSettings settings = new AppSettings();
            Shot(settings, Path.Combine(dir, "gpw-curve.png"), true);
            Shot(settings, Path.Combine(dir, "gpw-generic.png"), false);
            Console.WriteLine("ok: " + dir);
            return 0;
        }

        private static void Shot(AppSettings settings, string path, bool deviceCurve)
        {
            using (GpwSettingsForm form = new GpwSettingsForm(settings, "046D:C539", "G PRO Wireless", delegate { }, deviceCurve))
            {
                form.Show();
                Application.DoEvents();
                form.Refresh();
                Application.DoEvents();
                using (Bitmap bmp = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                    bmp.Save(path, ImageFormat.Png);
                }
                form.Hide();
            }
        }
    }
}
