using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;

/// <summary>
/// 图标预览工具：反射调用主程序里的 IconFactory.Create，把真实生成的托盘图标
/// 按 32px / 16px 两种尺寸放大画到一张对比图上，用来核对颜色与字形。
/// </summary>
internal static class IconPreview
{
    private static void Main(string[] args)
    {
        string exe = args.Length > 0 ? args[0] : @"bin\WirelessBattery.exe";
        string outDir = args.Length > 1 ? args[1] : "docs";

        Assembly asm = Assembly.LoadFrom(Path.GetFullPath(exe));
        Type factory = asm.GetType("WirelessBattery.IconFactory", true);
        MethodInfo create = factory.GetMethod("Create", BindingFlags.Public | BindingFlags.Static);
        if (create == null) throw new InvalidOperationException("IconFactory.Create 未找到");

        Color green = Color.FromArgb(34, 197, 94);
        Color blue = Color.FromArgb(59, 130, 246);
        Color red = Color.FromArgb(240, 68, 56);
        Color yellow = Color.FromArgb(250, 204, 21);
        Color gray = Color.FromArgb(152, 162, 179);

        // 标题、电量、颜色、是否充电
        object[][] cases =
        {
            new object[] { "75% 鼠标绿", (int?)75, green, false },
            new object[] { "24% 低电红", (int?)24, red, false },
            new object[] { "FC 鼠标绿", (int?)99, green, false },
            new object[] { "FC 耳机蓝", (int?)100, blue, false },
            new object[] { "充电中 有值", (int?)60, yellow, true },
            new object[] { "充电中 无值", null, yellow, true },
            new object[] { "未知 灰", null, gray, false },
            new object[] { "7% 红", (int?)7, red, false },
        };

        using (Bitmap sheet = new Bitmap(860, 360))
        {
            using (Graphics g = Graphics.FromImage(sheet))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.FromArgb(32, 33, 36));

                using (Font f = new Font("Segoe UI", 11f, FontStyle.Bold))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(228, 231, 236)))
                    g.DrawString("托盘图标 · 实际生成结果（上排 32px 放大 3 倍，下排 16px 放大 3 倍）", f, b, 20, 12);

                for (int i = 0; i < cases.Length; i++)
                {
                    object[] c = cases[i];
                    int x = 24 + i * 104;
                    using (Icon icon = (Icon)create.Invoke(null, new object[] { c[1], c[2], c[3] }))
                    using (Bitmap bmp = icon.ToBitmap())
                    {
                        g.InterpolationMode = InterpolationMode.NearestNeighbor;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        g.DrawImage(bmp, new Rectangle(x, 56, 96, 96));
                    }

                    using (Icon icon = (Icon)create.Invoke(null, new object[] { c[1], c[2], c[3] }))
                    using (Bitmap big = icon.ToBitmap())
                    using (Bitmap small = Downscale(big, 16))
                    {
                        g.InterpolationMode = InterpolationMode.NearestNeighbor;
                        g.DrawImage(small, new Rectangle(x, 176, 48, 48));
                    }

                    using (Font f = new Font("Segoe UI", 8.2f, FontStyle.Regular))
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(150, 156, 168)))
                    using (StringFormat sf = new StringFormat())
                    {
                        sf.Alignment = StringAlignment.Center;
                        g.DrawString((string)c[0], f, b, new RectangleF(x - 6, 232, 108, 20), sf);
                    }
                }

                // 放到深色任务栏上的观感
                using (SolidBrush b = new SolidBrush(Color.FromArgb(48, 52, 58)))
                    g.FillRectangle(b, 20, 264, 820, 70);

                for (int i = 0; i < cases.Length; i++)
                {
                    object[] c = cases[i];
                    int x = 30 + i * 104;
                    using (Icon icon = (Icon)create.Invoke(null, new object[] { c[1], c[2], c[3] }))
                    using (Bitmap bmp = icon.ToBitmap())
                    using (Bitmap small = Downscale(bmp, 16))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(small, new Rectangle(x, 288, 16, 16));
                        g.InterpolationMode = InterpolationMode.NearestNeighbor;
                        g.DrawImage(small, new Rectangle(x + 26, 288, 32, 32));
                    }
                }
            }

            string path = Path.Combine(outDir, "tray-icon-actual.png");
            sheet.Save(path, ImageFormat.Png);
            Console.WriteLine("ok: " + path);
        }
    }

    private static Bitmap Downscale(Bitmap src, int size)
    {
        Bitmap dst = new Bitmap(size, size);
        using (Graphics g = Graphics.FromImage(dst))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            g.DrawImage(src, new Rectangle(0, 0, size, size));
        }
        return dst;
    }
}
