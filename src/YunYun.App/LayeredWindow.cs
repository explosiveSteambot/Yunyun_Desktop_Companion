using System.Runtime.InteropServices;
using static YunYun.App.Native;

namespace YunYun.App;

/// <summary>
/// Borderless window with per-pixel alpha (UpdateLayeredWindow): transparent pixels are
/// see-through AND click-through. No taskbar button, never takes focus.
/// Content is pushed with <see cref="Present"/> as premultiplied BGRA bytes.
/// </summary>
public class LayeredWindow : Form
{
    IntPtr _memDc, _dib, _oldBmp, _bits;
    int _dibW, _dibH;

    public int WinX { get; private set; }
    public int WinY { get; private set; }
    public int WinW { get; private set; }
    public int WinH { get; private set; }

    public LayeredWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(1, 1);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEACTIVATE) { m.Result = MA_NOACTIVATE; return; }   // clicks don't steal focus
        base.WndProc(ref m);
    }

    /// <summary>Shows the picture (w x h premultiplied BGRA) with its top-left at screen (x, y).</summary>
    public void Present(byte[] bgra, int w, int h, int x, int y)
    {
        if (!IsHandleCreated) return;
        EnsureDib(w, h);
        Marshal.Copy(bgra, 0, _bits, w * h * 4);
        IntPtr screen = GetDC(IntPtr.Zero);
        try
        {
            var dst = new POINT(x, y);
            var size = new SIZE(w, h);
            var src = new POINT(0, 0);
            var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
            UpdateLayeredWindow(Handle, screen, ref dst, ref size, _memDc, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally { ReleaseDC(IntPtr.Zero, screen); }
        WinX = x; WinY = y; WinW = w; WinH = h;
    }

    public void Present(Frame f, int x, int y) => Present(f.Bgra, f.W, f.H, x, y);

    void EnsureDib(int w, int h)
    {
        if (_dib != IntPtr.Zero && w == _dibW && h == _dibH) return;
        FreeDib();
        IntPtr screen = GetDC(IntPtr.Zero);
        try
        {
            var bmi = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,                    // top-down rows
                biPlanes = 1,
                biBitCount = 32,
            };
            _dib = CreateDIBSection(screen, ref bmi, 0, out _bits, IntPtr.Zero, 0);
            _memDc = CreateCompatibleDC(screen);
            _oldBmp = SelectObject(_memDc, _dib);
            _dibW = w; _dibH = h;
        }
        finally { ReleaseDC(IntPtr.Zero, screen); }
    }

    void FreeDib()
    {
        if (_memDc != IntPtr.Zero) { SelectObject(_memDc, _oldBmp); DeleteDC(_memDc); _memDc = IntPtr.Zero; }
        if (_dib != IntPtr.Zero) { DeleteObject(_dib); _dib = IntPtr.Zero; }
    }

    protected override void Dispose(bool disposing)
    {
        FreeDib();
        base.Dispose(disposing);
    }
}
