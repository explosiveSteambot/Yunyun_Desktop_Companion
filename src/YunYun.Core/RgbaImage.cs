namespace YunYun.Core;

/// <summary>Float image, premultiplied alpha, RGBA interleaved, values 0..1.</summary>
public sealed class RgbaImage
{
    public int Width { get; }
    public int Height { get; }
    public float[] Data { get; }

    public RgbaImage(int width, int height)
    {
        Width = width;
        Height = height;
        Data = new float[width * height * 4];
    }

    /// <summary>From straight (non-premultiplied) 8-bit pixels; <paramref name="bgr"/> = byte order B,G,R,A.</summary>
    public static RgbaImage FromStraight8(int width, int height, ReadOnlySpan<byte> pixels, int stride, bool bgr)
    {
        var img = new RgbaImage(width, height);
        var d = img.Data;
        for (int y = 0; y < height; y++)
        {
            var row = pixels.Slice(y * stride, width * 4);
            for (int x = 0; x < width; x++)
            {
                int s = x * 4, o = (y * width + x) * 4;
                float a = row[s + 3] / 255f;
                float c0 = row[s] / 255f, c1 = row[s + 1] / 255f, c2 = row[s + 2] / 255f;
                d[o] = (bgr ? c2 : c0) * a;
                d[o + 1] = c1 * a;
                d[o + 2] = (bgr ? c0 : c2) * a;
                d[o + 3] = a;
            }
        }
        return img;
    }

    public void Clear() => Array.Clear(Data);

    /// <summary>Bounding box of pixels with alpha above the threshold.</summary>
    public RectI OpaqueBounds(float threshold = 0.5f)
    {
        int x0 = Width, y0 = Height, x1 = -1, y1 = -1;
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                if (Data[(y * Width + x) * 4 + 3] > threshold)
                {
                    if (x < x0) x0 = x;
                    if (x > x1) x1 = x;
                    if (y < y0) y0 = y;
                    if (y > y1) y1 = y;
                }
        return x1 < 0 ? new RectI(0, 0, 0, 0) : new RectI(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    /// <summary>Writes premultiplied BGRA bytes (Windows layered-window / PArgb layout), top-down.</summary>
    public void ToPremultipliedBgra(Span<byte> dst)
    {
        var d = Data;
        for (int i = 0, n = Width * Height; i < n; i++)
        {
            int s = i * 4;
            dst[s] = ToByte(d[s + 2]);
            dst[s + 1] = ToByte(d[s + 1]);
            dst[s + 2] = ToByte(d[s]);
            dst[s + 3] = ToByte(d[s + 3]);
        }
    }

    /// <summary>Writes straight-alpha RGBA bytes (for saving / debugging).</summary>
    public void ToStraightRgba(Span<byte> dst)
    {
        var d = Data;
        for (int i = 0, n = Width * Height; i < n; i++)
        {
            int s = i * 4;
            float a = d[s + 3];
            float inv = a > 1e-4f ? 1f / a : 0f;
            dst[s] = ToByte(d[s] * inv);
            dst[s + 1] = ToByte(d[s + 1] * inv);
            dst[s + 2] = ToByte(d[s + 2] * inv);
            dst[s + 3] = ToByte(a);
        }
    }

    static byte ToByte(float v) => (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);
}

/// <summary>Compositing and sampling helpers for <see cref="RgbaImage"/>.</summary>
public static class Raster
{
    /// <summary>Draws <paramref name="src"/> over <paramref name="dst"/> with its top-left at (x, y).</summary>
    public static void Over(RgbaImage dst, RgbaImage src, int x, int y)
    {
        int sx0 = Math.Max(0, -x), sy0 = Math.Max(0, -y);
        int sx1 = Math.Min(src.Width, dst.Width - x), sy1 = Math.Min(src.Height, dst.Height - y);
        var d = dst.Data;
        var s = src.Data;
        for (int sy = sy0; sy < sy1; sy++)
        {
            int so = (sy * src.Width + sx0) * 4;
            int doff = ((sy + y) * dst.Width + sx0 + x) * 4;
            for (int sx = sx0; sx < sx1; sx++, so += 4, doff += 4)
            {
                float a = s[so + 3];
                if (a <= 0f) continue;
                float k = 1f - a;
                d[doff] = s[so] + d[doff] * k;
                d[doff + 1] = s[so + 1] + d[doff + 1] * k;
                d[doff + 2] = s[so + 2] + d[doff + 2] * k;
                d[doff + 3] = a + d[doff + 3] * k;
            }
        }
    }

    /// <summary>Bilinear sample at (sx, sy), pixel centres at integer coordinates, transparent outside.</summary>
    public static void Sample(RgbaImage img, float sx, float sy, out float r, out float g, out float b, out float a)
    {
        r = g = b = a = 0f;
        int x0 = (int)MathF.Floor(sx), y0 = (int)MathF.Floor(sy);
        float fx = sx - x0, fy = sy - y0;
        if (x0 < -1 || y0 < -1 || x0 >= img.Width || y0 >= img.Height) return;
        Acc(img, x0, y0, (1 - fx) * (1 - fy), ref r, ref g, ref b, ref a);
        Acc(img, x0 + 1, y0, fx * (1 - fy), ref r, ref g, ref b, ref a);
        Acc(img, x0, y0 + 1, (1 - fx) * fy, ref r, ref g, ref b, ref a);
        Acc(img, x0 + 1, y0 + 1, fx * fy, ref r, ref g, ref b, ref a);
    }

    static void Acc(RgbaImage img, int x, int y, float w, ref float r, ref float g, ref float b, ref float a)
    {
        if (w <= 0f || x < 0 || y < 0 || x >= img.Width || y >= img.Height) return;
        int o = (y * img.Width + x) * 4;
        var d = img.Data;
        r += d[o] * w;
        g += d[o + 1] * w;
        b += d[o + 2] * w;
        a += d[o + 3] * w;
    }

    /// <summary>Composites one premultiplied pixel over dst at (x, y).</summary>
    public static void OverPixel(RgbaImage dst, int x, int y, float r, float g, float b, float a)
    {
        if (a <= 0f) return;
        int o = (y * dst.Width + x) * 4;
        var d = dst.Data;
        float k = 1f - a;
        d[o] = r + d[o] * k;
        d[o + 1] = g + d[o + 1] * k;
        d[o + 2] = b + d[o + 2] * k;
        d[o + 3] = a + d[o + 3] * k;
    }
}

/// <summary>
/// Scales a premultiplied float image to premultiplied BGRA bytes of another size.
/// Down-scaling uses area averaging (no aliasing on thin outlines), up-scaling bilinear.
/// Weights are precomputed once per size pair, so it is cheap to call every frame.
/// </summary>
public sealed class Resampler
{
    public int SrcW { get; }
    public int SrcH { get; }
    public int DstW { get; }
    public int DstH { get; }
    readonly (int Start, float[] W)[] _wx, _wy;

    public Resampler(int srcW, int srcH, int dstW, int dstH)
    {
        SrcW = srcW; SrcH = srcH; DstW = Math.Max(1, dstW); DstH = Math.Max(1, dstH);
        _wx = Weights(SrcW, DstW);
        _wy = Weights(SrcH, DstH);
    }

    static (int, float[])[] Weights(int src, int dst)
    {
        var res = new (int, float[])[dst];
        double s = (double)src / dst;
        for (int i = 0; i < dst; i++)
        {
            if (s >= 1.0)
            {   // area: source interval [i*s, (i+1)*s)
                double a = i * s, b = (i + 1) * s;
                int i0 = (int)Math.Floor(a), i1 = Math.Min(src, (int)Math.Ceiling(b));
                var w = new float[Math.Max(1, i1 - i0)];
                for (int k = i0; k < i1; k++) w[k - i0] = (float)((Math.Min(b, k + 1) - Math.Max(a, k)) / s);
                res[i] = (i0, w);
            }
            else
            {   // bilinear between the two nearest source pixels
                double c = (i + 0.5) * s - 0.5;
                int i0 = (int)Math.Floor(c);
                float f = (float)(c - i0);
                if (i0 < 0) { i0 = 0; f = 0; }
                if (i0 >= src - 1) { i0 = src - 1; f = 0; }
                res[i] = (i0, f > 0 ? new[] { 1 - f, f } : new[] { 1f });
            }
        }
        return res;
    }

    /// <summary>dst = DstW*DstH*4 bytes, premultiplied BGRA, top-down.</summary>
    public void Run(RgbaImage src, byte[] dst)
    {
        var d = src.Data;
        for (int y = 0; y < DstH; y++)
        {
            var (y0, wy) = _wy[y];
            for (int x = 0; x < DstW; x++)
            {
                var (x0, wx) = _wx[x];
                float r = 0, g = 0, b = 0, a = 0;
                for (int j = 0; j < wy.Length; j++)
                {
                    int row = (y0 + j) * SrcW;
                    float wj = wy[j];
                    for (int i = 0; i < wx.Length; i++)
                    {
                        float w = wj * wx[i];
                        int o = (row + x0 + i) * 4;
                        r += d[o] * w; g += d[o + 1] * w; b += d[o + 2] * w; a += d[o + 3] * w;
                    }
                }
                int q = (y * DstW + x) * 4;
                dst[q] = B(b); dst[q + 1] = B(g); dst[q + 2] = B(r); dst[q + 3] = B(a);
            }
        }
    }

    static byte B(float v) => (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);
}
