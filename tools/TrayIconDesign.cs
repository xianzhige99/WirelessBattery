using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

/// <summary>
/// 托盘电量图标样式稿渲染器（独立工具，不参与主程序构建）。
/// 七段字形用几何绘制：每段两端切 45° 斜角、段间留均匀缝隙，贴近计算器 LED 数码管。
/// 满电显示 FC；无描边；充电中为两个数字位各自的中横线（左右并排）。
/// </summary>
internal static class TrayIconDesign
{
    private static readonly Color Green = Color.FromArgb(34, 197, 94);
    private static readonly Color Red = Color.FromArgb(239, 68, 68);
    private static readonly Color Yellow = Color.FromArgb(250, 204, 21);

    // 七段命名：a=上 b=右上 c=右下 d=下 e=左下 f=左上 g=中
    private static readonly string[] DigitMasks =
    {
        "abcdef", "bc", "abdeg", "abcdg", "bcfg", "acdfg", "acdefg", "abc", "abcdefg", "abcdfg"
    };

    // 字母（满电 FC）：F = a f g e；C = a f e d
    private const string MaskF = "afge";
    private const string MaskC = "afed";

    // 32px 画布内的字形尺寸
    private const float H = 26.0f;          // 字高（32px 画布内留 3px 边距）
    private const float W = 13.6f;          // 字宽（宽高比 0.52）
    private const float Spacing = 1.8f;     // 字间距

    private static void Main(string[] args)
    {
        string outDir = args != null && args.Length > 0 ? args[0] : AppDomain.CurrentDomain.BaseDirectory;
        int SW = 1000, SH = 822;

        float t0 = 3.20f;                    // 笔画粗细（字高的 12.3%）
        float gapA = 1.00f;                  // 相邻尖角之间留 1px

        using (Bitmap sheet = new Bitmap(SW, SH))
        {
            using (Graphics g = Graphics.FromImage(sheet))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.FromArgb(252, 252, 253));

                using (Font f = new Font("Segoe UI", 15f, FontStyle.Bold))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(24, 24, 27)))
                    g.DrawString("托盘电量图标 · 七段字形 v8（四角尖角对合 · 中间留 1px）", f, b, 30, 18);

                using (Font f = new Font("Segoe UI", 9f, FontStyle.Regular))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(113, 118, 132)))
                    g.DrawString("每段两端各切 45°，两个切面在轴线上汇成一个 90° 尖角，尖角就是段的端点；每段两端都是 90° 尖角；相邻尖角朝同一交点收拢，只在中心空出 1px，四个尖角分列其上下左右。"
                        + "满电 FC、无描边、充电中为两个数字位的中横线；A 区平滑大图，B/C 为 32px 与 16px 真实像素。",
                        f, b, 30, 48);

                string[] texts = { "75", "47", "24", "9", "FC", "CHG" };
                string[] labels = { "75%", "47%", "24%", "9%", "满电 FC", "充电中" };

                // ---------- A · 平滑大图 ----------
                Caption(g, 30, 76, "A · 字形（128px 平滑渲染，看形状）");
                DrawRow(g, 44, 102, texts, labels, 128, 1, t0, gapA, true);

                // ---------- B · 32px 真实像素 ----------
                Caption(g, 30, 262, "B · 32px 实际像素（4 倍最近邻放大，任务栏真实大小）");
                DrawRow(g, 44, 288, texts, labels, 128, 4, t0, gapA, false);

                // ---------- C · 16px 真实像素 ----------
                Caption(g, 30, 448, "C · 16px 实际像素（高 DPI 未缩放时系统取的小图）");
                DrawRow(g, 44, 474, texts, labels, 64, 4, t0, gapA, false, true);

                // ---------- D · 粗细对比 ----------
                Caption(g, 30, 582, "D · 尖角间隙对比（1.0 / 1.4 / 2.0px，每档左为 32px、右为 16px 实拍）");
                float[] gaps = { 1.0f, 1.4f, 2.0f };
                string[] gnames = { "尖角间隙 1.0px", "1.4px", "2.0px" };
                for (int v = 0; v < gaps.Length; v++)
                {
                    int bx = 44 + v * 312;
                    string[] pick = { "75", "24", "CHG" };
                    for (int i = 0; i < pick.Length; i++)
                    {
                        using (Bitmap icon = Render(pick[i], t0, gaps[v]))
                        {
                            g.InterpolationMode = InterpolationMode.NearestNeighbor;
                            g.PixelOffsetMode = PixelOffsetMode.Half;
                            g.DrawImage(icon, new Rectangle(bx + i * 84, 608, 48, 48));
                            using (Bitmap small = Downscale(icon, 16))
                                g.DrawImage(small, new Rectangle(bx + i * 84 + 50, 616, 26, 26));
                        }
                    }
                    using (Font f = new Font("Segoe UI", 8f, FontStyle.Regular))
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(90, 96, 110)))
                        g.DrawString(gnames[v], f, b, bx, 660);
                }

                // ---------- E · 任务栏条带 ----------
                Caption(g, 30, 682, "E · 放到任务栏上的实际观感");
                DrawStrip(g, 30, 702, t0, gapA, Color.FromArgb(32, 33, 36), "深色任务栏", texts);
                DrawStrip(g, 30, 746, t0, gapA, Color.FromArgb(243, 244, 246), "浅色任务栏", texts);

                using (Font f = new Font("Segoe UI", 8.2f, FontStyle.Regular))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(140, 146, 160)))
                    g.DrawString("配色：鼠标绿 / 电量 < 30% 红 / 充电中黄 / 满电 FC 绿。",
                        f, b, 30, SH - 20);
            }

            string path = Path.Combine(outDir, "tray-icon-design.png");
            sheet.Save(path, ImageFormat.Png);
            Console.WriteLine("ok: " + path);

            // 单字细节：256px 平滑渲染，专门用来看斜切与缝隙的成形
            using (Bitmap zoom = new Bitmap(760, 300))
            using (Graphics g = Graphics.FromImage(zoom))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.FromArgb(32, 33, 36));

                using (Font f = new Font("Segoe UI", 10f, FontStyle.Regular))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(150, 156, 168)))
                {
                    g.DrawString("单字细节 · 256px（看斜切端头与缝隙）", f, b, 24, 14);
                    
                }

                using (Bitmap big = RenderLarge("8", t0, gapA, 220))
                    g.DrawImage(big, 30, 40);
                using (Bitmap big = RenderLarge("24", t0, gapA, 220))
                    g.DrawImage(big, 320, 40);

                using (Font f = new Font("Segoe UI", 9f, FontStyle.Regular))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(150, 156, 168)))
                {
                    g.DrawString("8", f, b, 130, 266);
                    g.DrawString("24", f, b, 420, 266);
                }

                string zp = Path.Combine(outDir, "tray-icon-zoom.png");
                zoom.Save(zp, ImageFormat.Png);
                Console.WriteLine("ok: " + zp);
            }
        }
    }

    private static void Caption(Graphics g, int x, int y, string text)
    {
        using (Font f = new Font("Segoe UI", 10.2f, FontStyle.Bold))
        using (SolidBrush b = new SolidBrush(Color.FromArgb(55, 65, 81)))
            g.DrawString(text, f, b, x, y);
    }

    private static void DrawRow(Graphics g, int x, int y, string[] texts, string[] labels,
        int cell, int pixelScale, float t, float gap, bool smooth, bool small = false)
    {
        for (int i = 0; i < texts.Length; i++)
        {
            int cx = x + i * 156;
            Bitmap icon = smooth ? RenderLarge(texts[i], t, gap, cell) : Render(texts[i], t, gap, small);
            int w = smooth ? cell : (small ? 16 : 32);

            using (icon)
            {
                g.InterpolationMode = smooth ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(icon, new Rectangle(cx, y, w * pixelScale, w * pixelScale));

                using (Font f = new Font("Segoe UI", 8.6f, FontStyle.Regular))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(90, 96, 110)))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    g.DrawString(labels[i], f, b, new RectangleF(cx - 14, y + w * pixelScale + 4, 156, 20), sf);
                }
            }
        }
    }

    private static void DrawStrip(Graphics g, int x, int y, float t, float gap, Color bg, string caption, string[] texts)
    {
        int w = 940, h = 40;
        using (GraphicsPath p = Round(new Rectangle(x, y, w, h), 8))
        using (SolidBrush b = new SolidBrush(bg))
            g.FillPath(b, p);

        Color cap = bg.GetBrightness() > 0.5f ? Color.FromArgb(120, 126, 140) : Color.FromArgb(150, 156, 168);
        using (Font f = new Font("Segoe UI", 8f, FontStyle.Regular))
        using (SolidBrush b = new SolidBrush(cap))
            g.DrawString(caption, f, b, x + 12, y + 11);

        for (int i = 0; i < texts.Length; i++)
        {
            int ix = x + 150 + i * 128;
            using (Bitmap icon = Render(texts[i], t, gap))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.DrawImage(icon, new Rectangle(ix, y + 4, 32, 32));
                using (Bitmap tiny = Downscale(icon, 16))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(tiny, new Rectangle(ix + 42, y + 12, 16, 16));
                }
            }
        }
    }

    private static Bitmap Render(string text, float t, float gap, bool small = false)
    {
        Bitmap bmp = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            if (text == "CHG") DrawCharging(g, 1f, t, gap);
            else DrawText(g, text, 1f, t, gap);
        }
        return small ? Downscale(bmp, 16) : bmp;
    }

    private static Bitmap RenderLarge(string text, float t, float gap, int size)
    {
        float scale = size / 32f;
        Bitmap bmp = new Bitmap(size, size);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            if (text == "CHG") DrawCharging(g, scale, t, gap);
            else DrawText(g, text, scale, t, gap);
        }
        return bmp;
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

    private static Color ColorFor(string text)
    {
        if (text == "CHG") return Yellow;
        if (text == "FC") return Green;
        int v;
        if (int.TryParse(text, out v) && v < 30) return Red;
        return Green;
    }

    private static void DrawText(Graphics g, string text, float scale, float t, float gap)
    {
        int n = text.Length;
        float totalW = n * W + (n - 1) * Spacing;
        float x0 = (32f - totalW) / 2f;
        float y0 = (32f - H) / 2f;

        GraphicsState st = g.Save();
        g.ScaleTransform(scale, scale);
        using (SolidBrush b = new SolidBrush(ColorFor(text)))
        {
            for (int i = 0; i < n; i++)
            {
                string mask = MaskOf(text[i]);
                if (mask == null) continue;
                Digit(g, b, x0 + i * (W + Spacing), y0, W, H, t, gap, mask);
            }
        }
        g.Restore(st);
    }

    private static string MaskOf(char c)
    {
        if (c >= '0' && c <= '9') return DigitMasks[c - '0'];
        if (c == 'F') return MaskF;
        if (c == 'C') return MaskC;
        return null;
    }

    /// <summary>
    /// 按七段掩码把一个字画出来。
    /// 每一段两端都收成 90° 尖角（两条 45° 切面在轴线上相交）；相邻段的尖角朝同一个
    /// 交点收拢，彼此只隔 gap —— 于是贴合处中间空出约 gap 的一小块，四个尖角分列其上下左右。
    /// </summary>
    private static void Digit(Graphics g, Brush b, float x, float y, float w, float h, float t, float gap, string mask)
    {
        float half = t * 0.5f;                  // 半笔画
        float c = half;                         // 斜切长度 = 半笔画 → 尖角 90°
        float hg = gap * 0.5f;                  // 尖角离交点的退让量
        float yMidCy = y + (h - t) * 0.5f + half;   // 中横段轴线
        float aCy = y + half;                       // 上横段轴线
        float dCy = y + h - half;                   // 下横段轴线
        float lCx = x + half;                       // 左竖柱轴线
        float rCx = x + w - half;                   // 右竖柱轴线

        if (mask.IndexOf('a') >= 0) HBar(g, b, lCx + hg, rCx - hg, aCy, t, c);
        if (mask.IndexOf('g') >= 0) HBar(g, b, lCx + hg, rCx - hg, yMidCy, t, c);
        if (mask.IndexOf('d') >= 0) HBar(g, b, lCx + hg, rCx - hg, dCy, t, c);

        if (mask.IndexOf('f') >= 0) VBar(g, b, lCx, aCy + hg, yMidCy - hg, t, c);
        if (mask.IndexOf('b') >= 0) VBar(g, b, rCx, aCy + hg, yMidCy - hg, t, c);
        if (mask.IndexOf('e') >= 0) VBar(g, b, lCx, yMidCy + hg, dCy - hg, t, c);
        if (mask.IndexOf('c') >= 0) VBar(g, b, rCx, yMidCy + hg, dCy - hg, t, c);
    }

    /// <summary>横段：两端各切 45°，收成 90° 尖角。x1/x2 是两个尖角的位置。</summary>
    private static void HBar(Graphics g, Brush b, float x1, float x2, float cy, float t, float c)
    {
        float len = x2 - x1;
        if (len <= 0.2f || t <= 0.2f) return;
        c = Math.Min(c, len * 0.49f);
        float y = cy - t * 0.5f;
        g.FillPolygon(b, new PointF[]
        {
            new PointF(x1 + c, y),
            new PointF(x2 - c, y),
            new PointF(x2, cy),
            new PointF(x2 - c, y + t),
            new PointF(x1 + c, y + t),
            new PointF(x1, cy)
        });
    }

    /// <summary>竖段：两端各切 45°，收成 90° 尖角。y1/y2 是两个尖角的位置。</summary>
    private static void VBar(Graphics g, Brush b, float cx, float y1, float y2, float t, float c)
    {
        float len = y2 - y1;
        if (len <= 0.2f || t <= 0.2f) return;
        c = Math.Min(c, len * 0.49f);
        float x = cx - t * 0.5f;
        g.FillPolygon(b, new PointF[]
        {
            new PointF(cx, y1),
            new PointF(x + t, y1 + c),
            new PointF(x + t, y2 - c),
            new PointF(cx, y2),
            new PointF(x, y2 - c),
            new PointF(x, y1 + c)
        });
    }

    /// <summary>充电中：两个数字位各自的中横线，左右并排。</summary>
    private static void DrawCharging(Graphics g, float scale, float t, float gap)
    {
        float totalW = 2 * W + Spacing;
        float x0 = (32f - totalW) / 2f;
        float y0 = (32f - H) / 2f;
        float yMid = y0 + (H - t) / 2f;

        GraphicsState st = g.Save();
        g.ScaleTransform(scale, scale);
        using (SolidBrush b = new SolidBrush(Yellow))
        {
            // 两根横段并排，两端同样收 90° 尖角
            float half = t * 0.5f, hg = gap * 0.5f;
            HBar(g, b, x0 + half + hg, x0 + W - half - hg, yMid + half, t, half);
            HBar(g, b, x0 + W + Spacing + half + hg, x0 + 2 * W + Spacing - half - hg, yMid + half, t, half);
        }
        g.Restore(st);
    }

    private static GraphicsPath Round(Rectangle r, int radius)
    {
        int d = Math.Max(1, radius * 2);
        GraphicsPath p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
