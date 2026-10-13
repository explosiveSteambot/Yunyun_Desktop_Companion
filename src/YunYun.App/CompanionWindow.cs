using System.Diagnostics;
using System.Runtime.InteropServices;
using YunYun.Core;
using static YunYun.App.Native;

namespace YunYun.App;

/// <summary>The companion: resting animation, ragdoll while dragged, reaction pictures.</summary>
sealed class CompanionWindow : LayeredWindow
{
    public event Action? HideRequested, SettingsRequested, QuitRequested;
    public event Action? GrabStarted, GrabEnded, BinEntered;
    /// <summary>Time to play a "being held" sound (1 s after the grab, then every 5-10 s).</summary>
    public event Action? GrabSoundDue;
    /// <summary>The Recycle Bin picture has been shown long enough.</summary>
    public event Action? ReactionFinished;

    const int IdleFrameMs = 30;                 // the resting GIF runs at 30 ms per frame
    const int BinTolerance = 6;                 // px around the Recycle Bin icon
    const double GrabFirstSound = 1.0;          // s after the grab
    const double GrabSoundMin = 5.0, GrabSoundMax = 10.0;   // s between the following ones
    const double ReactionSeconds = 1.5;         // how long a Recycle Bin picture is shown

    // Timing is checked in the frame loop, not with separate WinForms timers: WM_TIMER is only
    // generated when the message queue is empty, and while she is dragged the queue never is
    // (mouse moves + 60 fps frames), so a second timer would simply never fire.
    readonly Random _rnd = new();
    double _nextGrabSound = double.PositiveInfinity;
    double _reactionEnd = double.PositiveInfinity;

    Settings _s;
    readonly Ragdoll _rd;
    readonly System.Windows.Forms.Timer _timer = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    double _last;

    List<Frame> _idle = new();
    List<Frame> _reactions = new();
    RectI _idleVisible;
    Resampler? _resampler;
    byte[] _dragBuf = Array.Empty<byte>();
    int _idleIndex = -1;
    RectI? _bin;

    public bool Dragging { get; private set; }
    public bool Reacting { get; private set; }

    public CompanionWindow(Settings settings, Ragdoll ragdoll)
    {
        _s = settings;
        _rd = ragdoll;
        _timer.Tick += (_, _) => Tick();
        ContextMenuStrip = BuildMenu();
    }

    double Dpi => DeviceDpi / 96.0;
    double IdleScale => _s.IdleScale * Dpi;
    double DragScale => _s.Scale * Dpi;

    ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip();
        m.Items.Add("Спрятать в трей", null, (_, _) => HideRequested?.Invoke());
        m.Items.Add("Настройки…", null, (_, _) => SettingsRequested?.Invoke());
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Выход", null, (_, _) => QuitRequested?.Invoke());
        m.Opening += (_, e) => e.Cancel = Dragging || Reacting;
        return m;
    }

    // ------------------------------------------------------------------ setup
    void LoadPictures()
    {
        _idle = Assets.List("idle", ".png").Select(p => Assets.LoadFrame(p, IdleScale)).ToList();
        _reactions = Assets.List("reactions", ".png").Select(p => Assets.LoadFrame(p, IdleScale)).ToList();
        var u = _idle[0].Visible;                   // opaque part of the whole animation
        foreach (var f in _idle)
        {
            int x0 = Math.Min(u.X, f.Visible.X), y0 = Math.Min(u.Y, f.Visible.Y);
            int x1 = Math.Max(u.Right, f.Visible.Right), y1 = Math.Max(u.Bottom, f.Visible.Bottom);
            u = new RectI(x0, y0, x1 - x0, y1 - y0);
        }
        _idleVisible = u;
        _idleIndex = -1;
    }

    /// <summary>Call once after the handle exists (DPI is known then).</summary>
    public void Start()
    {
        if (!IsHandleCreated) CreateHandle();
        if (_idle.Count == 0) { LoadPictures(); PlaceInitial(); }
        Show();
        WinLayer.Apply(this, _s.Layer);
        _last = _clock.Elapsed.TotalSeconds;
        _timer.Interval = Math.Max(1, 1000 / _s.Fps);
        _timer.Start();
        PresentIdle(force: true);
    }

    public void Stop()
    {
        _timer.Stop();
        if (Dragging) EndDrag();
        Hide();
    }

    /// <summary>True once the pictures are loaded and the window has a real position.</summary>
    public bool HasPosition => _idle.Count > 0;

    public void PlaceInitial()
    {
        if (_idle.Count == 0) return;               // never shown yet: Start() will place her
        var f = _idle[0];
        int x, y;
        if (_s.RememberPosition && _s.Position is { Length: 2 } p) { x = p[0]; y = p[1]; }
        else
        {
            var a = Screen.PrimaryScreen!.WorkingArea;
            x = a.Right - f.W - 40;
            y = a.Bottom - f.H - 40;
        }
        MoveIdle(x, y);
        ClampToWorkArea();
        PresentIdle(force: true);
    }

    public void ApplySettings(Settings s)
    {
        var old = _s;
        _s = s;
        if (Math.Abs(s.IdleScale - old.IdleScale) > 1e-9 && _idle.Count > 0)
        {
            int cx = _idleX + _idle[0].W / 2, cy = _idleY + _idle[0].H / 2;
            LoadPictures();
            MoveIdle(cx - _idle[0].W / 2, cy - _idle[0].H / 2);
            ClampToWorkArea();
            if (!Dragging && !Reacting) PresentIdle(force: true);
        }
        _resampler = null;                           // drag size may have changed
        _timer.Interval = Math.Max(1, 1000 / s.Fps);
        if (Visible && !Dragging && !Reacting) WinLayer.Apply(this, s.Layer);
    }

    public int[] RestingPosition => new[] { _idleX, _idleY };

    // ------------------------------------------------------------------ geometry
    static List<RectI> WorkAreas() =>
        Screen.AllScreens.Select(s => new RectI(s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height)).ToList();

    int _idleX, _idleY;
    void MoveIdle(int x, int y) { _idleX = x; _idleY = y; }

    /// <summary>Keep the resting character on the desktop work area (never over the taskbar).</summary>
    void ClampToWorkArea()
    {
        var f = _idle[0];
        var rect = new RectI(_idleX, _idleY, f.W, f.H);
        var area = ScreenGeometry.PickArea(WorkAreas(), _idleX + _idleVisible.X + _idleVisible.W / 2, _idleY + _idleVisible.Y + _idleVisible.H / 2);
        var r = ScreenGeometry.ClampRect(rect, area, _idleVisible);
        MoveIdle(r.X, r.Y);
    }

    void PresentIdle(bool force = false)
    {
        if (_idle.Count == 0) return;
        int k = (int)(_clock.ElapsedMilliseconds / IdleFrameMs) % _idle.Count;
        if (!force && k == _idleIndex) return;
        _idleIndex = k;
        Present(_idle[k], _idleX, _idleY);
    }

    // ------------------------------------------------------------------ drag
    (int X, int Y) WindowPosFor(Point cursor) =>
        ((int)Math.Round(cursor.X - _rd.GrabPoint.X * DragScale), (int)Math.Round(cursor.Y - _rd.GrabPoint.Y * DragScale));

    void StartDrag(Point cursor)
    {
        if (Reacting || Dragging) return;
        _bin = BinRect();                            // icons may have moved since last time
        Dragging = true;
        var (x, y) = WindowPosFor(cursor);
        _rd.Reset(x / DragScale, y / DragScale);      // physics works in rig pixels
        _last = _clock.Elapsed.TotalSeconds;
        WinLayer.Apply(this, _s.Layer);           // keep the chosen layer: in desktop mode she stays under windows
        RenderDrag(x, y);
        _nextGrabSound = _last + GrabFirstSound;
        GrabStarted?.Invoke();
    }

    void EndDrag()
    {
        if (!Dragging) return;
        Dragging = false;
        _nextGrabSound = double.PositiveInfinity;
        GrabEnded?.Invoke();
        int cx = WinX + WinW / 2, cy = WinY + WinH / 2;
        MoveIdle(cx - _idle[0].W / 2, cy - _idle[0].H / 2);
        ClampToWorkArea();
        PresentIdle(force: true);
        WinLayer.Apply(this, _s.Layer);
    }

    void RenderDrag(int x, int y)
    {
        int w = (int)Math.Round(_rd.Width * DragScale), h = (int)Math.Round(_rd.Height * DragScale);
        if (_resampler == null || _resampler.DstW != w || _resampler.DstH != h)
        {
            _resampler = new Resampler(_rd.Width, _rd.Height, w, h);
            _dragBuf = new byte[_resampler.DstW * _resampler.DstH * 4];
        }
        _resampler.Run(_rd.Render(), _dragBuf);
        Present(_dragBuf, _resampler.DstW, _resampler.DstH, x, y);
    }

    // ------------------------------------------------------------------ recycle bin
    RectI? BinRect()
    {
        if (_s.BinRect is { Length: 4 } r) return new RectI(r[0], r[1], r[2], r[3]);
        return RecycleBin.Find();
    }

    /// <summary>Replaces the character with reaction picture i, centred on the Recycle Bin.</summary>
    public void ShowReaction(int i)
    {
        Dragging = false;
        Reacting = true;
        _nextGrabSound = double.PositiveInfinity;
        _reactionEnd = _clock.Elapsed.TotalSeconds + ReactionSeconds;
        var f = _reactions.Count > 0 ? _reactions[Math.Min(i, _reactions.Count - 1)] : _idle[0];
        var b = _bin ?? new RectI(WinX + WinW / 2, WinY + WinH / 2, 0, 0);
        int cx = b.X + b.W / 2, cy = b.Y + b.H / 2;
        var area = ScreenGeometry.PickArea(WorkAreas(), cx, cy);
        var r = ScreenGeometry.ClampRect(new RectI(cx - f.W / 2, cy - f.H / 2, f.W, f.H), area);   // whole picture on screen
        WinLayer.Apply(this, _s.Layer);           // keep the chosen layer: in desktop mode she stays under windows
        Present(f, r.X, r.Y);
    }

    /// <summary>Back to the resting animation in the top-left corner of the work area.</summary>
    public void FinishReaction()
    {
        Reacting = false;
        _reactionEnd = double.PositiveInfinity;
        var area = ScreenGeometry.PickArea(WorkAreas(), WinX + WinW / 2, WinY + WinH / 2);
        MoveIdle(area.X - _idleVisible.X, area.Y - _idleVisible.Y);   // visible part touches the corner
        PresentIdle(force: true);
        WinLayer.Apply(this, _s.Layer);
    }

    // ------------------------------------------------------------------ loop
    void Tick()
    {
        double now = _clock.Elapsed.TotalSeconds, dt = now - _last;
        _last = now;
        if (Dragging)
        {
            if ((MouseButtons & MouseButtons.Left) == 0) { EndDrag(); return; }   // released outside
            var cur = Cursor.Position;
            var (x, y) = WindowPosFor(cur);
            _rd.Step(dt, x / DragScale, y / DragScale);
            RenderDrag(x, y);
            if (_bin is RectI bin && bin.Inflate(BinTolerance).Contains(cur.X, cur.Y))
            {
                Dragging = false;
                _nextGrabSound = double.PositiveInfinity;
                GrabEnded?.Invoke();
                BinEntered?.Invoke();                 // the controller decides what happens next
            }
            else if (now >= _nextGrabSound)
            {
                _nextGrabSound = now + GrabSoundMin + _rnd.NextDouble() * (GrabSoundMax - GrabSoundMin);
                GrabSoundDue?.Invoke();
            }
        }
        else if (Reacting)
        {
            if (now >= _reactionEnd)
            {
                _reactionEnd = double.PositiveInfinity;
                ReactionFinished?.Invoke();           // the controller calls FinishReaction (or quits)
            }
        }
        else
        {
            PresentIdle();
        }
    }

    // ------------------------------------------------------------------ input
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) StartDrag(Cursor.Position);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left) EndDrag();
    }

    protected override void WndProc(ref Message m)
    {
        // "On the desktop" mode: whatever raises the window (a click, a grab, Alt+Tab ...) - keep it at the bottom.
        if (m.Msg == WM_WINDOWPOSCHANGING && _s.Layer == Layers.Desktop)
        {
            var wp = Marshal.PtrToStructure<WINDOWPOS>(m.LParam);
            if ((wp.flags & SWP_NOZORDER) == 0)
            {
                wp.hwndInsertAfter = HWND_BOTTOM;
                Marshal.StructureToPtr(wp, m.LParam, false);
            }
        }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
