using YunYun.Core;
using static YunYun.App.Native;

namespace YunYun.App;

/// <summary>Z-order of the companion: on the desktop (under windows) / normal / always on top.</summary>
static class WinLayer
{
    const uint Flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE;

    public static void Apply(Form w, string layer)
    {
        if (!w.IsHandleCreated) return;
        switch (layer)
        {
            case Layers.Top:
                SetWindowPos(w.Handle, HWND_TOPMOST, 0, 0, 0, 0, Flags);
                break;
            case Layers.Desktop:
                SetWindowPos(w.Handle, HWND_NOTOPMOST, 0, 0, 0, 0, Flags);
                SetWindowPos(w.Handle, HWND_BOTTOM, 0, 0, 0, 0, Flags);
                break;
            default:
                SetWindowPos(w.Handle, HWND_NOTOPMOST, 0, 0, 0, 0, Flags);
                break;
        }
    }
}
