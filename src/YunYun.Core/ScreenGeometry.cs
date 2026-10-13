namespace YunYun.Core;

/// <summary>Integer rectangle in screen pixels.</summary>
public readonly record struct RectI(int X, int Y, int W, int H)
{
    public int Right => X + W;
    public int Bottom => Y + H;
    public bool Contains(int px, int py) => px >= X && px < Right && py >= Y && py < Bottom;
    public RectI Inflate(int m) => new(X - m, Y - m, W + 2 * m, H + 2 * m);
}

/// <summary>Keeping the companion on the desktop work area (never over the taskbar).</summary>
public static class ScreenGeometry
{
    /// <summary>Work area that contains the point, else the nearest one (multi-monitor).</summary>
    public static RectI PickArea(IReadOnlyList<RectI> areas, int px, int py)
    {
        if (areas.Count == 0) throw new ArgumentException("no screens", nameof(areas));
        foreach (var a in areas)
            if (a.Contains(px, py)) return a;
        RectI best = areas[0];
        long bestD = long.MaxValue;
        foreach (var a in areas)
        {
            long cx = Math.Clamp(px, a.X, a.Right - 1), cy = Math.Clamp(py, a.Y, a.Bottom - 1);
            long d = (cx - px) * (cx - px) + (cy - py) * (cy - py);
            if (d < bestD) { bestD = d; best = a; }
        }
        return best;
    }

    /// <summary>
    /// Move (not resize) <paramref name="rect"/> so that its visible part lies inside <paramref name="area"/>.
    /// <paramref name="visible"/> is the opaque part relative to the rect's top-left; null = whole rect.
    /// </summary>
    public static RectI ClampRect(RectI rect, RectI area, RectI? visible = null)
    {
        var v = visible ?? new RectI(0, 0, rect.W, rect.H);
        int left = rect.X + v.X, top = rect.Y + v.Y;
        int right = left + v.W, bottom = top + v.H;
        int dx = 0, dy = 0;
        if (right > area.Right) dx = area.Right - right;
        if (left + dx < area.X) dx = area.X - left;
        if (bottom > area.Bottom) dy = area.Bottom - bottom;
        if (top + dy < area.Y) dy = area.Y - top;
        return rect with { X = rect.X + dx, Y = rect.Y + dy };
    }
}
