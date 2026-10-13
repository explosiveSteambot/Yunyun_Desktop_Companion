"""Z-order of the companion window: on the desktop / normal / always on top.

Windows: set directly with SetWindowPos, which is reliable and does not recreate
the window. The window never activates on a click (WS_EX_NOACTIVATE via
Qt.WindowDoesNotAcceptFocus), and in desktop mode every attempt to change its
Z-order (a click, a grab, Alt+Tab ...) is turned into "to the bottom" right in
WM_WINDOWPOSCHANGING (see keep_bottom_on, called from Companion.nativeEvent).
The layer is the same while she is dragged: in desktop mode she stays under windows.

Other systems (Linux/X11, macOS): Qt window hints. Changing hints recreates the
native window, so this is done only when the setting changes.
"""
import sys

from PySide6.QtCore import Qt

from .config import LAYER_DESKTOP, LAYER_TOP

IS_WIN = sys.platform.startswith("win")

if IS_WIN:
    import ctypes
    from ctypes import wintypes

    _user32 = ctypes.windll.user32
    _user32.SetWindowPos.argtypes = [wintypes.HWND, wintypes.HWND, ctypes.c_int, ctypes.c_int,
                                     ctypes.c_int, ctypes.c_int, ctypes.c_uint]
    HWND_TOP, HWND_BOTTOM, HWND_TOPMOST, HWND_NOTOPMOST = 0, 1, -1, -2
    SWP_NOSIZE, SWP_NOMOVE, SWP_NOACTIVATE = 0x0001, 0x0002, 0x0010
    _FLAGS = SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE

    SWP_NOZORDER = 0x0004
    WM_WINDOWPOSCHANGING = 0x0046

    class _WINDOWPOS(ctypes.Structure):
        _fields_ = [("hwnd", wintypes.HWND), ("hwndInsertAfter", wintypes.HWND),
                    ("x", ctypes.c_int), ("y", ctypes.c_int), ("cx", ctypes.c_int), ("cy", ctypes.c_int),
                    ("flags", wintypes.UINT)]

    def _set(widget, after):
        _user32.SetWindowPos(wintypes.HWND(int(widget.winId())), wintypes.HWND(after), 0, 0, 0, 0, _FLAGS)


def keep_bottom_on(event_type, message):
    """Desktop mode, from QWidget.nativeEvent: any Z-order change of the window -> to the bottom."""
    if not IS_WIN or bytes(event_type) != b"windows_generic_MSG":
        return
    msg = wintypes.MSG.from_address(int(message))
    if msg.message == WM_WINDOWPOSCHANGING and msg.lParam:
        wp = _WINDOWPOS.from_address(msg.lParam)
        if not wp.flags & SWP_NOZORDER:
            wp.hwndInsertAfter = HWND_BOTTOM


BASE_FLAGS = Qt.FramelessWindowHint | Qt.Tool | Qt.NoDropShadowWindowHint   # Tool: no taskbar button
if IS_WIN:
    BASE_FLAGS |= Qt.WindowDoesNotAcceptFocus      # WS_EX_NOACTIVATE: a click does not activate/raise her


def initial_flags(layer):
    """Window flags to construct the widget with."""
    f = BASE_FLAGS
    if not IS_WIN:
        if layer == LAYER_DESKTOP:
            f |= Qt.WindowStaysOnBottomHint
        elif layer == LAYER_TOP:
            f |= Qt.WindowStaysOnTopHint
    return f


def apply(widget, layer):
    """Put the (already shown) widget on the requested layer."""
    if IS_WIN:
        if layer == LAYER_TOP:
            _set(widget, HWND_TOPMOST)
        elif layer == LAYER_DESKTOP:
            _set(widget, HWND_NOTOPMOST)
            _set(widget, HWND_BOTTOM)
        else:
            _set(widget, HWND_NOTOPMOST)
        return
    want = initial_flags(layer)
    hints = Qt.WindowStaysOnBottomHint | Qt.WindowStaysOnTopHint
    if (widget.windowFlags() & hints) != (want & hints):   # only touch it when the layer really changes
        visible = widget.isVisible()
        widget.setWindowFlags(want)       # recreates the native window
        if visible:
            widget.show()


def reassert_bottom(widget):
    """Desktop mode on Windows: push back under other windows after a click raised her."""
    if IS_WIN:
        _set(widget, HWND_BOTTOM)
