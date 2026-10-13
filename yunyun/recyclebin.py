"""Where the Recycle Bin icon is on the desktop (Windows).

The desktop is a ListView control (SysListView32) owned by explorer.exe. Its
items live in another process, so the item text / rectangle are read through a
small buffer allocated inside explorer (VirtualAllocEx + ReadProcessMemory) -
the standard way to query the desktop icons.

The Recycle Bin is found by its display name, which is taken from the shell
itself (so it works with any UI language: "Корзина", "Recycle Bin", ...).

Returns rectangles in *physical* screen pixels; the caller converts them to Qt
(logical) pixels.
"""
import sys

IS_WIN = sys.platform.startswith("win")


def recycle_bin_rect():
    """(x, y, w, h) of the Recycle Bin icon in physical pixels, or None."""
    if not IS_WIN:
        return None
    try:
        return _find()
    except Exception:
        return None


if IS_WIN:
    import ctypes
    from ctypes import wintypes

    user32 = ctypes.WinDLL("user32", use_last_error=True)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    shell32 = ctypes.WinDLL("shell32")
    ole32 = ctypes.WinDLL("ole32")

    user32.FindWindowW.restype = wintypes.HWND
    user32.FindWindowW.argtypes = [wintypes.LPCWSTR, wintypes.LPCWSTR]
    user32.FindWindowExW.restype = wintypes.HWND
    user32.FindWindowExW.argtypes = [wintypes.HWND, wintypes.HWND, wintypes.LPCWSTR, wintypes.LPCWSTR]
    user32.SendMessageW.restype = ctypes.c_ssize_t
    user32.SendMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
    user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
    user32.ClientToScreen.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.POINT)]
    kernel32.OpenProcess.restype = wintypes.HANDLE
    kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel32.VirtualAllocEx.restype = ctypes.c_void_p
    kernel32.VirtualAllocEx.argtypes = [wintypes.HANDLE, ctypes.c_void_p, ctypes.c_size_t, wintypes.DWORD, wintypes.DWORD]
    kernel32.VirtualFreeEx.argtypes = [wintypes.HANDLE, ctypes.c_void_p, ctypes.c_size_t, wintypes.DWORD]
    kernel32.ReadProcessMemory.argtypes = [wintypes.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
    kernel32.WriteProcessMemory.argtypes = [wintypes.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]

    LVM_FIRST = 0x1000
    LVM_GETITEMCOUNT = LVM_FIRST + 4
    LVM_GETITEMRECT = LVM_FIRST + 14
    LVM_GETITEMTEXTW = LVM_FIRST + 115
    LVIF_TEXT = 0x1
    LVIR_ICON = 1
    PROCESS_ACCESS = 0x0008 | 0x0010 | 0x0020 | 0x0400   # VM_OPERATION | VM_READ | VM_WRITE | QUERY_INFORMATION
    MEM_COMMIT, MEM_RESERVE, MEM_RELEASE, PAGE_READWRITE = 0x1000, 0x2000, 0x8000, 0x04

    class LVITEMW(ctypes.Structure):
        _fields_ = [("mask", wintypes.UINT), ("iItem", ctypes.c_int), ("iSubItem", ctypes.c_int),
                    ("state", wintypes.UINT), ("stateMask", wintypes.UINT), ("pszText", ctypes.c_void_p),
                    ("cchTextMax", ctypes.c_int), ("iImage", ctypes.c_int), ("lParam", wintypes.LPARAM),
                    ("iIndent", ctypes.c_int), ("iGroupId", ctypes.c_int), ("cColumns", wintypes.UINT),
                    ("puColumns", ctypes.c_void_p), ("piColFmt", ctypes.c_void_p), ("iGroup", ctypes.c_int)]

    class SHFILEINFOW(ctypes.Structure):
        _fields_ = [("hIcon", wintypes.HANDLE), ("iIcon", ctypes.c_int), ("dwAttributes", wintypes.DWORD),
                    ("szDisplayName", wintypes.WCHAR * 260), ("szTypeName", wintypes.WCHAR * 80)]

    def _bin_name():
        pidl = ctypes.c_void_p()
        if shell32.SHGetSpecialFolderLocation(None, 0x000A, ctypes.byref(pidl)) != 0:   # CSIDL_BITBUCKET
            return None
        try:
            info = SHFILEINFOW()
            shell32.SHGetFileInfoW.argtypes = [ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(SHFILEINFOW), wintypes.UINT, wintypes.UINT]
            shell32.SHGetFileInfoW(pidl, 0, ctypes.byref(info), ctypes.sizeof(info), 0x8 | 0x200)  # SHGFI_PIDL | SHGFI_DISPLAYNAME
            return info.szDisplayName or None
        finally:
            ole32.CoTaskMemFree(pidl)

    def _desktop_listview():
        def find_in(parent):
            dv = user32.FindWindowExW(parent, None, "SHELLDLL_DefView", None)
            return user32.FindWindowExW(dv, None, "SysListView32", None) if dv else None
        lv = find_in(user32.FindWindowW("Progman", None))
        w = None
        while not lv:                                      # with a wallpaper slideshow it sits under a WorkerW
            w = user32.FindWindowExW(None, w, "WorkerW", None)
            if not w:
                break
            lv = find_in(w)
        return lv

    def _find():
        name = _bin_name()
        lv = _desktop_listview()
        if not name or not lv:
            return None
        pid = wintypes.DWORD()
        user32.GetWindowThreadProcessId(lv, ctypes.byref(pid))
        hp = kernel32.OpenProcess(PROCESS_ACCESS, False, pid.value)
        if not hp:
            return None
        size = 4096
        remote = kernel32.VirtualAllocEx(hp, None, size, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE)
        try:
            n = user32.SendMessageW(lv, LVM_GETITEMCOUNT, 0, 0)
            text_off = ctypes.sizeof(LVITEMW)
            for i in range(n):
                item = LVITEMW(mask=LVIF_TEXT, iItem=i, iSubItem=0, pszText=remote + text_off, cchTextMax=260)
                kernel32.WriteProcessMemory(hp, remote, ctypes.byref(item), ctypes.sizeof(item), None)
                user32.SendMessageW(lv, LVM_GETITEMTEXTW, i, remote)
                buf = ctypes.create_unicode_buffer(260)
                kernel32.ReadProcessMemory(hp, remote + text_off, buf, ctypes.sizeof(buf), None)
                if buf.value != name:
                    continue
                rc = wintypes.RECT(LVIR_ICON, 0, 0, 0)       # left = which part of the item
                kernel32.WriteProcessMemory(hp, remote, ctypes.byref(rc), ctypes.sizeof(rc), None)
                if not user32.SendMessageW(lv, LVM_GETITEMRECT, i, remote):
                    return None
                kernel32.ReadProcessMemory(hp, remote, ctypes.byref(rc), ctypes.sizeof(rc), None)
                p = wintypes.POINT(rc.left, rc.top)
                user32.ClientToScreen(lv, ctypes.byref(p))
                return (p.x, p.y, rc.right - rc.left, rc.bottom - rc.top)
            return None
        finally:
            kernel32.VirtualFreeEx(hp, remote, 0, MEM_RELEASE)
            kernel32.CloseHandle(hp)
