using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using YunYun.Core;

namespace YunYun.App;

/// <summary>A ready-to-present picture: premultiplied BGRA bytes + its opaque area.</summary>
public sealed record Frame(int W, int H, byte[] Bgra, RectI Visible);

/// <summary>
/// Access to the resources. All pictures, sounds and the rig come from res.dat (built from
/// assets_src/ by tools/PackAssets), which is embedded into the exe as a single resource and
/// read into memory once. Only the program icon is embedded on its own.
/// </summary>
public static class Assets
{
    static readonly Assembly Asm = typeof(Assets).Assembly;
    static readonly Lazy<ResourcePack> Pack = new(() =>
    {
        using var s = Asm.GetManifestResourceStream("res.dat")
                      ?? throw new FileNotFoundException("res.dat is not embedded - run tools/PackAssets (see README)");
        return ResourcePack.Load(s);
    });

    /// <summary>path like "rig/body.png" (as packed from assets_src/).</summary>
    public static Stream Open(string path) => Pack.Value.Open(path);

    public static IEnumerable<string> List(string folder, string ext) => Pack.Value.List(folder, ext);

    /// <summary>The program icon (embedded separately: it is also the exe's own icon).</summary>
    public static Icon AppIcon()
    {
        using var s = Asm.GetManifestResourceStream("app.ico")!;
        return new Icon(s);
    }

    public static string ReadText(string path)
    {
        using var s = Open(path);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    public static byte[] ReadBytes(string path) => Pack.Value.Read(path);

    static Bitmap LoadBitmap(string path)
    {
        using var s = Open(path);
        using var tmp = new Bitmap(s);
        return new Bitmap(tmp);                     // detached from the stream
    }

    /// <summary>Rig layer as a premultiplied float image (used by the ragdoll).</summary>
    public static RgbaImage LoadRgba(string path)
    {
        using var bmp = LoadBitmap(path);
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);   // B,G,R,A straight
        try
        {
            var buf = new byte[data.Stride * bmp.Height];
            Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            return RgbaImage.FromStraight8(bmp.Width, bmp.Height, buf, data.Stride, bgr: true);
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>Picture scaled with GDI+ into a ready-to-present frame.</summary>
    public static Frame LoadFrame(string path, double scale)
    {
        using var src = LoadBitmap(path);
        int w = Math.Max(1, (int)Math.Round(src.Width * scale)), h = Math.Max(1, (int)Math.Round(src.Height * scale));
        using var dst = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(dst))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var attr = new ImageAttributes();
            attr.SetWrapMode(WrapMode.TileFlipXY);   // no dark fringe at the borders
            g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attr);
        }
        var data = dst.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        var bytes = new byte[w * h * 4];
        try
        {
            for (int y = 0; y < h; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * w * 4, w * 4);
        }
        finally { dst.UnlockBits(data); }
        return new Frame(w, h, bytes, AlphaBounds(bytes, w, h));
    }

    public static RectI AlphaBounds(byte[] bgra, int w, int h, byte threshold = 8)
    {
        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (bgra[(y * w + x) * 4 + 3] > threshold)
                {
                    if (x < x0) x0 = x;
                    if (x > x1) x1 = x;
                    if (y < y0) y0 = y;
                    if (y > y1) y1 = y;
                }
        return x1 < 0 ? new RectI(0, 0, w, h) : new RectI(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }
}
