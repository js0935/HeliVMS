using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

const string OutDir = @"D:\HeliVms\Assets";
Directory.CreateDirectory(OutDir);

static Bitmap NewLogo(int size)
{
    var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.Clear(Color.Transparent);

    float s = size / 256f;
    var rect = new RectangleF(8 * s, 8 * s, 240 * s, 240 * s);

    using var path = RoundedRect(rect, 36 * s);
    using (var bg = new LinearGradientBrush(rect, Color.FromArgb(255, 11, 27, 46), Color.FromArgb(255, 28, 58, 99), 135f))
        g.FillPath(bg, path);
    using (var border = new Pen(Color.FromArgb(90, 120, 180, 255), 2 * s))
        g.DrawPath(border, path);

    float cx = 128 * s, cy = 118 * s;
    using (var ring = new Pen(Color.FromArgb(255, 214, 232, 255), 9 * s))
        g.DrawEllipse(ring, cx - 62 * s, cy - 62 * s, 124 * s, 124 * s);
    using (var lens = new SolidBrush(Color.FromArgb(255, 7, 14, 32)))
        g.FillEllipse(lens, cx - 48 * s, cy - 48 * s, 96 * s, 96 * s);
    using (var inner = new Pen(Color.FromArgb(200, 38, 96, 170), 3 * s))
        g.DrawEllipse(inner, cx - 36 * s, cy - 36 * s, 72 * s, 72 * s);
    using (var hi = new SolidBrush(Color.FromArgb(220, 255, 255, 255)))
    {
        g.FillEllipse(hi, cx - 20 * s, cy - 26 * s, 14 * s, 14 * s);
        g.FillEllipse(hi, cx - 26 * s, cy - 12 * s, 6 * s, 6 * s);
    }

    using (var stub = new Pen(Color.FromArgb(160, 170, 205, 255), 5 * s)
    {
        StartCap = LineCap.Round,
        EndCap = LineCap.Round
    })
    {
        g.DrawLine(stub, cx - 46 * s, 34 * s, cx - 46 * s, 56 * s);
        g.DrawLine(stub, cx + 46 * s, 34 * s, cx + 46 * s, 56 * s);
        g.DrawLine(stub, cx, 28 * s, cx, 52 * s);
    }

    using var font = new Font("Segoe UI Semibold", 26 * s, FontStyle.Bold, GraphicsUnit.Pixel);
    using var fg = new SolidBrush(Color.FromArgb(255, 235, 243, 255));
    using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
    g.DrawString("HeliVms", font, fg, new RectangleF(0, 190 * s, size, 52 * s), sf);
    return bmp;
}

static GraphicsPath RoundedRect(RectangleF r, float d)
{
    var p = new GraphicsPath();
    p.AddArc(r.X, r.Y, d, d, 180, 90);
    p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
    p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
    p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
    p.CloseFigure();
    return p;
}

// --- PNG 256 ---
using (var logo = NewLogo(256))
    logo.Save(Path.Combine(OutDir, "app_logo.png"), ImageFormat.Png);

// --- ICO（多尺寸 PNG 內嵌）---
var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
var blobs = new List<byte[]>();
foreach (var sz in sizes)
{
    using var b = NewLogo(sz);
    using var ms = new MemoryStream();
    b.Save(ms, ImageFormat.Png);
    blobs.Add(ms.ToArray());
}

using var fs = File.Create(Path.Combine(OutDir, "app.ico"));
using var bw = new BinaryWriter(fs);
bw.Write((ushort)0); bw.Write((ushort)1); bw.Write((ushort)sizes.Length);
int offset = 6 + 16 * sizes.Length;
for (int i = 0; i < sizes.Length; i++)
{
    byte w = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
    bw.Write(w); bw.Write(w);
    bw.Write((byte)0); bw.Write((byte)0);
    bw.Write((ushort)1); bw.Write((ushort)32);
    bw.Write((uint)blobs[i].Length);
    bw.Write((uint)offset);
    offset += blobs[i].Length;
}
foreach (var b in blobs) bw.Write(b);
bw.Flush();

// --- Splash 960x540 ---
const int sw = 960, sh = 540;
using (var splash = new Bitmap(sw, sh, PixelFormat.Format32bppArgb))
{
    using var g = Graphics.FromImage(splash);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    using (var gb = new LinearGradientBrush(new Rectangle(0, 0, sw, sh),
               Color.FromArgb(255, 9, 15, 30), Color.FromArgb(255, 16, 34, 64), 160f))
        g.FillRectangle(gb, 0, 0, sw, sh);
    using (var logo = NewLogo(256))
        g.DrawImage(logo, sw / 2 - 130, 110, 260, 260);

    using var fw = new Font("Microsoft JhengHei", 40, FontStyle.Bold, GraphicsUnit.Pixel);
    using var fs2 = new Font("Microsoft JhengHei", 22, FontStyle.Regular, GraphicsUnit.Pixel);
    using var fm = new Font("Microsoft JhengHei", 15, FontStyle.Regular, GraphicsUnit.Pixel);
    using var white = new SolidBrush(Color.FromArgb(255, 235, 243, 255));
    using var blue = new SolidBrush(Color.FromArgb(255, 120, 175, 255));
    using var dim = new SolidBrush(Color.FromArgb(255, 130, 150, 180));
    using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
    g.DrawString("HeliVms", fw, white, new RectangleF(0, 370, sw, 70), sf);
    g.DrawString("智慧 32 路 NVR/VMS 錄影系統", fs2, blue, new RectangleF(0, 436, sw, 40), sf);
    g.DrawString("禾秝軟體開發團隊", fm, dim, new RectangleF(0, 486, sw, 30), sf);
    splash.Save(Path.Combine(OutDir, "splash.png"), ImageFormat.Png);
}

Console.WriteLine("Logo / ICO / Splash 已完成：" + OutDir);