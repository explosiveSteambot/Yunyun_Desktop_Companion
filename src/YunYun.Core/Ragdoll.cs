namespace YunYun.Core;

/// <summary>
/// The cut-out character: static layers + moving parts driven by the window motion.
///
///     var rd = new Ragdoll(rig, file => LoadImage(file));
///     rd.Reset(wx, wy);                  // canvas origin on screen, in rig pixels
///     each tick: rd.Step(dt, wx, wy);    // new origin; velocities are derived from it
///                rd.Render();            // -> rd.Canvas (premultiplied RGBA floats)
/// </summary>
public sealed class Ragdoll
{
    public const double SubStep = 1.0 / 240.0;

    public int Width { get; }
    public int Height { get; }
    public (int X, int Y) GrabPoint { get; }
    public RgbaImage Canvas { get; }
    public IReadOnlyDictionary<string, Part> Parts => _parts;

    readonly Dictionary<string, Part> _parts = new();
    readonly Dictionary<string, (RgbaImage Img, int X, int Y)> _statics = new();
    readonly string[] _order;
    double _ox, _oy, _acc;

    public Ragdoll(RigDefinition rig, Func<string, RgbaImage> loadImage, IReadOnlyDictionary<string, PartParams>? parameters = null)
    {
        parameters ??= PartParams.Defaults;
        Width = rig.Size[0];
        Height = rig.Size[1];
        GrabPoint = (rig.GrabPoint[0], rig.GrabPoint[1]);
        Canvas = new RgbaImage(Width, Height);
        _statics["body"] = (loadImage(rig.Body.File), rig.Body.Offset[0], rig.Body.Offset[1]);
        if (rig.Back != null) _statics["back"] = (loadImage(rig.Back.File), rig.Back.Offset[0], rig.Back.Offset[1]);
        if (rig.Cover != null) _statics["cover"] = (loadImage(rig.Cover.File), rig.Cover.Offset[0], rig.Cover.Offset[1]);
        foreach (var (name, def) in rig.Parts)
        {
            var img = loadImage(def.File);
            var p = parameters.TryGetValue(name, out var pp) ? pp : new PartParams(new DriveParams(0, 0, 0, 0, 0.1));
            _parts[name] = def.Type == "chain" ? new ChainPart(name, def, img, p) : new RigidPart(name, def, img, p);
        }
        _order = rig.DrawOrder;
        Reset(0, 0);
    }

    public void Reset(double wx, double wy)
    {
        _ox = wx;
        _oy = wy;
        _acc = 0;
        foreach (var p in _parts.Values) p.Reset();
    }

    /// <summary>Advance by dt seconds; (wx, wy) = current canvas origin on screen in rig pixels.</summary>
    public void Step(double dt, double wx, double wy)
    {
        dt = Math.Min(dt, 0.1);
        _acc += dt;
        int n = (int)(_acc / SubStep);
        _acc -= n * SubStep;
        double inv = 1.0 / Math.Max(dt, 1e-4);
        double vx = (wx - _ox) * inv, vy = (wy - _oy) * inv;
        for (int s = 0; s < n; s++)
            foreach (var p in _parts.Values)
                p.Step(vx, vy, SubStep);
        _ox = wx;
        _oy = wy;
    }

    /// <summary>Draws the current pose into <see cref="Canvas"/>.</summary>
    public RgbaImage Render()
    {
        Canvas.Clear();
        foreach (var k in _order)
        {
            if (_statics.TryGetValue(k, out var st)) Raster.Over(Canvas, st.Img, st.X, st.Y);
            else if (_parts.TryGetValue(k, out var p)) p.Draw(Canvas);
        }
        return Canvas;
    }
}

/// <summary>A moving part of the rig.</summary>
public abstract class Part
{
    public string Name { get; }
    protected readonly RgbaImage Img;
    protected readonly double OffX, OffY;     // top-left of the image in canvas px
    protected readonly DriveParams Drive;
    protected readonly bool DriveInDegrees;
    double _vsx, _vsy;
    protected double Lean;

    /// <summary>Forces the lean (tests / calibration); null = computed from the motion.</summary>
    public double? LeanOverride { get; set; }

    protected Part(string name, RigPart def, RgbaImage img, PartParams p, bool degrees)
    {
        Name = name;
        Img = img;
        OffX = def.Offset[0];
        OffY = def.Offset[1];
        Drive = p.Drive;
        DriveInDegrees = degrees;
    }

    public virtual void Reset()
    {
        _vsx = _vsy = 0;
        Lean = 0;
    }

    /// <summary>Updates the lean; returns the value to use (radians).</summary>
    protected double UpdateLean(double vx, double vy, double h)
    {
        double k = 1.0 - Math.Exp(-h / Math.Max(Drive.Tau, 1e-4));
        _vsx += k * (vx - _vsx);
        _vsy += k * (vy - _vsy);
        double conv = DriveInDegrees ? Math.PI / 180.0 : 1.0;
        double max = Drive.Max * conv, rate = Drive.Rate * conv;
        double target = Math.Clamp((Drive.Kx * _vsx + Drive.Ky * _vsy) * conv, -max, max);
        double lim = rate * h;
        Lean += Math.Clamp(target - Lean, -lim, lim);
        return LeanOverride ?? Lean;
    }

    public abstract void Step(double vx, double vy, double h);
    public abstract void Draw(RgbaImage canvas);

    protected static double Wrap(double a)
    {
        a = (a + Math.PI) % (2 * Math.PI);
        if (a < 0) a += 2 * Math.PI;
        return a - Math.PI;
    }
}

/// <summary>Arms, legs, wing: rotate about a pivot by the lean angle.</summary>
public sealed class RigidPart : Part
{
    readonly double _pivX, _pivY;
    public double Angle { get; private set; }   // radians, + = clockwise on screen

    public RigidPart(string name, RigPart def, RgbaImage img, PartParams p) : base(name, def, img, p, degrees: true)
    {
        var piv = def.Pivot ?? throw new InvalidDataException($"{name}: rigid part needs a pivot");
        _pivX = piv[0];
        _pivY = piv[1];
    }

    public override void Reset()
    {
        base.Reset();
        Angle = 0;
    }

    public override void Step(double vx, double vy, double h) => Angle = UpdateLean(vx, vy, h);

    public override void Draw(RgbaImage canvas)
    {
        double c = Math.Cos(Angle), s = Math.Sin(Angle);
        double px = _pivX - OffX, py = _pivY - OffY;           // pivot in image coords
        // image -> canvas: p' = R (p - piv) + pivot ; bounding box of the rotated image
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (ix, iy) in new[] { (0.0, 0.0), (Img.Width, 0.0), (0.0, Img.Height), (Img.Width, Img.Height) })
        {
            double dx = ix - px, dy = iy - py;
            double cx = c * dx - s * dy + _pivX, cy = s * dx + c * dy + _pivY;
            minX = Math.Min(minX, cx); maxX = Math.Max(maxX, cx);
            minY = Math.Min(minY, cy); maxY = Math.Max(maxY, cy);
        }
        int x0 = Math.Max(0, (int)Math.Floor(minX) - 1), x1 = Math.Min(canvas.Width, (int)Math.Ceiling(maxX) + 2);
        int y0 = Math.Max(0, (int)Math.Floor(minY) - 1), y1 = Math.Min(canvas.Height, (int)Math.Ceiling(maxY) + 2);
        for (int y = y0; y < y1; y++)
        {
            double dy = y - _pivY;
            for (int x = x0; x < x1; x++)
            {
                double dx = x - _pivX;
                // canvas -> image: R^T (p - pivot) + piv
                float sx = (float)(c * dx + s * dy + px), sy = (float)(-s * dx + c * dy + py);
                Raster.Sample(Img, sx, sy, out var r, out var g, out var b, out var a);
                Raster.OverPixel(canvas, x, y, r, g, b, a);
            }
        }
    }
}

/// <summary>Hair strands and the tail: a chain of links that bends; the image is warped along it.</summary>
public sealed class ChainPart : Part
{
    const int Grid = 3;                         // px step of the coarse deformation map

    readonly int _n;                            // number of links
    readonly double[] _restX, _restY;           // rest joints, canvas px
    readonly double[] _len, _restAbs, _restRel;
    readonly double[] _omega, _leanW, _inertia, _maxBend;
    readonly double _rootLimit, _zeta, _follow, _inputTau;
    readonly float[] _segHw;
    readonly double _halfWidth;
    readonly double[] _dev, _w;                 // angle of each link relative to rest, and its rate
    readonly double[] _x, _y;                   // current joints, canvas px
    double _vfx, _vfy;

    public ChainPart(string name, RigPart def, RgbaImage img, PartParams p) : base(name, def, img, p, degrees: false)
    {
        var pts = def.Points ?? throw new InvalidDataException($"{name}: chain needs points");
        _n = pts.Length - 1;
        _restX = pts.Select(q => q[0]).ToArray();
        _restY = pts.Select(q => q[1]).ToArray();
        _len = new double[_n];
        _restAbs = new double[_n];
        _restRel = new double[_n];
        for (int i = 0; i < _n; i++)
        {
            double dx = _restX[i + 1] - _restX[i], dy = _restY[i + 1] - _restY[i];
            _len[i] = Math.Sqrt(dx * dx + dy * dy);
            _restAbs[i] = Math.Atan2(dy, dx);
            _restRel[i] = i == 0 ? _restAbs[0] : Wrap(_restAbs[i] - _restAbs[i - 1]);
        }
        _omega = new double[_n];
        _leanW = new double[_n];
        _inertia = new double[_n];
        double wsum = 0, rem = 0;
        for (int i = _n - 1; i >= 0; i--)
        {
            rem += _len[i];
            _inertia[i] = p.Inertia / Math.Max(rem * 0.5, 1.0);
        }
        for (int i = 0; i < _n; i++)
        {
            double t = _n > 1 ? (double)i / (_n - 1) : 0;
            _omega[i] = 2 * Math.PI / (p.PeriodRoot + (p.PeriodTip - p.PeriodRoot) * t);
            _leanW[i] = p.LeanAtRoot ? 2.0 + (0.3 - 2.0) * t : 1.0 + t;
            wsum += _leanW[i];
        }
        for (int i = 0; i < _n; i++) _leanW[i] /= wsum;
        var mb = p.MaxBend ?? new[] { 16.0 };
        _maxBend = new double[_n];                   // index = joint i (link i relative to link i-1)
        for (int i = 1; i < _n; i++) _maxBend[i] = mb[Math.Min(i - 1, mb.Length - 1)] * Math.PI / 180.0;
        _rootLimit = p.RootLimit * Math.PI / 180.0;
        _zeta = p.Zeta;
        _follow = p.Follow;
        _inputTau = p.InputTau;
        _segHw = (def.SegHalfWidth ?? Enumerable.Repeat(def.HalfWidth, _n).ToArray()).Select(v => (float)v).ToArray();
        _halfWidth = def.HalfWidth;
        _dev = new double[_n];
        _w = new double[_n];
        _x = (double[])_restX.Clone();
        _y = (double[])_restY.Clone();
    }

    public override void Reset()
    {
        base.Reset();
        Array.Clear(_dev);
        Array.Clear(_w);
        _vfx = _vfy = 0;
        Rebuild();
    }

    public override void Step(double vx, double vy, double h)
    {
        double lean = UpdateLean(vx, vy, h);
        double k = 1.0 - Math.Exp(-h / _inputTau);
        double ox = _vfx, oy = _vfy;
        _vfx += k * (vx - _vfx);
        _vfy += k * (vy - _vfy);
        double ax = (_vfx - ox) / h, ay = (_vfy - oy) / h;

        double parentAlpha = 0, phi = _restAbs[0];
        for (int i = 0; i < _n; i++)
        {
            double phiI = phi + _dev[i];
            double perp = -Math.Sin(phiI) * ax + Math.Cos(phiI) * ay;
            double om = _omega[i];
            double alpha = -om * om * (_dev[i] - lean * _leanW[i]) - 2 * _zeta * om * _w[i]
                           - _inertia[i] * perp - _follow * parentAlpha;
            _w[i] += alpha * h;
            _dev[i] += _w[i] * h;
            double lim = i == 0 ? _rootLimit : _maxBend[i];
            if (_dev[i] > lim) { _dev[i] = lim; _w[i] = Math.Min(_w[i], 0); }
            else if (_dev[i] < -lim) { _dev[i] = -lim; _w[i] = Math.Max(_w[i], 0); }
            parentAlpha = alpha;
            phi = phiI + (i + 1 < _n ? _restRel[i + 1] : 0);
        }
        Rebuild();
    }

    void Rebuild()
    {
        _x[0] = _restX[0];
        _y[0] = _restY[0];
        double phi = _restAbs[0];
        for (int i = 0; i < _n; i++)
        {
            phi += _dev[i];
            _x[i + 1] = _x[i] + _len[i] * Math.Cos(phi);
            _y[i + 1] = _y[i] + _len[i] * Math.Sin(phi);
            if (i + 1 < _n) phi += _restRel[i + 1];
        }
    }

    public override void Draw(RgbaImage canvas)
    {
        double hw = _halfWidth;
        int x0 = (int)Math.Max(_x.Min() - hw - 2, 0), x1 = (int)Math.Min(_x.Max() + hw + 3, canvas.Width);
        int y0 = (int)Math.Max(_y.Min() - hw - 2, 0), y1 = (int)Math.Min(_y.Max() + hw + 3, canvas.Height);
        if (x1 <= x0 || y1 <= y0) return;

        // coarse map: for each grid point, where to sample in the part image (NaN = outside the strand)
        int gw = (x1 - x0) / Grid + 2, gh = (y1 - y0) / Grid + 2;
        var mx = new float[gw * gh];
        var my = new float[gw * gh];
        var far = new float[gw * gh];
        for (int gy = 0; gy < gh; gy++)
            for (int gx = 0; gx < gw; gx++)
            {
                int o = gy * gw + gx;
                Map(x0 + gx * Grid, y0 + gy * Grid, out mx[o], out my[o], out far[o]);
            }

        for (int y = y0; y < y1; y++)
        {
            float fy = (float)(y - y0) / Grid;
            int gy = Math.Min((int)fy, gh - 2);
            float ty = fy - gy;
            for (int x = x0; x < x1; x++)
            {
                float fx = (float)(x - x0) / Grid;
                int gx = Math.Min((int)fx, gw - 2);
                float tx = fx - gx;
                int o00 = gy * gw + gx, o10 = o00 + 1, o01 = o00 + gw, o11 = o01 + 1;
                float w00 = (1 - tx) * (1 - ty), w10 = tx * (1 - ty), w01 = (1 - tx) * ty, w11 = tx * ty;
                float f = far[o00] * w00 + far[o10] * w10 + far[o01] * w01 + far[o11] * w11;
                if (f > 0.5f) continue;
                float sx = mx[o00] * w00 + mx[o10] * w10 + mx[o01] * w01 + mx[o11] * w11;
                float sy = my[o00] * w00 + my[o10] * w10 + my[o01] * w01 + my[o11] * w11;
                Raster.Sample(Img, sx, sy, out var r, out var g, out var b, out var a);
                Raster.OverPixel(canvas, x, y, r, g, b, a);
            }
        }
    }

    /// <summary>
    /// Backward map: find the nearest point of the current chain, express the pixel as
    /// (segment, position along it, signed distance across it) and take the same spot on the rest chain.
    /// </summary>
    void Map(double px, double py, out float sx, out float sy, out float far)
    {
        int best = 0;
        double bestD2 = double.MaxValue, bestT = 0;
        for (int j = 0; j < _n; j++)
        {
            double ax = _x[j], ay = _y[j], abx = _x[j + 1] - ax, aby = _y[j + 1] - ay;
            double l2 = abx * abx + aby * aby;
            double t = ((px - ax) * abx + (py - ay) * aby) / l2;
            double tc = Math.Clamp(t, 0, 1);
            if (j == 0) tc = Math.Min(t, 1);              // extend before the root
            if (j == _n - 1) tc = Math.Max(t, 0);         // and past the tip
            double cx = ax + tc * abx - px, cy = ay + tc * aby - py;
            double d2 = cx * cx + cy * cy;
            if (d2 < bestD2) { bestD2 = d2; best = j; bestT = tc; }
        }
        {
            int j = best;
            double ax = _x[j], ay = _y[j], abx = _x[j + 1] - ax, aby = _y[j + 1] - ay;
            double l = Math.Sqrt(abx * abx + aby * aby);
            double ux = abx / l, uy = aby / l;
            double rx = px - (ax + bestT * abx), ry = py - (ay + bestT * aby);
            double tang = rx * ux + ry * uy;
            double nrm = rx * -uy + ry * ux;
            double rax = _restX[j], ray = _restY[j], rbx = _restX[j + 1] - rax, rby = _restY[j + 1] - ray;
            double rl = Math.Sqrt(rbx * rbx + rby * rby);
            double rux = rbx / rl, ruy = rby / rl;
            sx = (float)(rax + bestT * rbx + tang * rux - nrm * ruy - OffX);
            sy = (float)(ray + bestT * rby + tang * ruy + nrm * rux - OffY);
            far = Math.Sqrt(bestD2) > _segHw[j] + 1.5 ? 1f : 0f;
        }
    }
}
