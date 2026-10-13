using System.Runtime.InteropServices;
using YunYun.Core;
using static YunYun.App.Native;

namespace YunYun.App;

/// <summary>
/// Where the Recycle Bin icon is on the desktop.
///
/// The desktop is a ListView (SysListView32) owned by explorer.exe. Its items live in
/// another process, so item text / rectangle are read through a small buffer allocated
/// inside explorer (VirtualAllocEx + Read/WriteProcessMemory) - the standard way.
/// The Bin is found by its display name taken from the shell, so the UI language does not matter.
/// Coordinates are physical screen pixels (the app is per-monitor DPI aware).
/// </summary>
static class RecycleBin
{
    const uint LVM_FIRST = 0x1000, LVM_GETITEMCOUNT = LVM_FIRST + 4, LVM_GETITEMRECT = LVM_FIRST + 14, LVM_GETITEMTEXTW = LVM_FIRST + 115;
    const uint LVIF_TEXT = 1;
    const int LVIR_ICON = 1, CSIDL_BITBUCKET = 0x000A;
    const uint PROCESS_ACCESS = 0x0008 | 0x0010 | 0x0020 | 0x0400;
    const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_RELEASE = 0x8000, PAGE_READWRITE = 0x04;

    [StructLayout(LayoutKind.Sequential)]
    struct LVITEMW
    {
        public uint mask;
        public int iItem, iSubItem;
        public uint state, stateMask;
        public IntPtr pszText;
        public int cchTextMax, iImage;
        public IntPtr lParam;
        public int iIndent, iGroupId;
        public uint cColumns;
        public IntPtr puColumns, piColFmt;
        public int iGroup;
    }

    public static RectI? Find()
    {
        try { return FindCore(); }
        catch (Exception) { return null; }       // never let a shell quirk crash the companion
    }

    static string? BinName()
    {
        if (SHGetSpecialFolderLocation(IntPtr.Zero, CSIDL_BITBUCKET, out var pidl) != 0) return null;
        try
        {
            var info = new SHFILEINFO();
            SHGetFileInfo(pidl, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), 0x8 | 0x200);  // SHGFI_PIDL | SHGFI_DISPLAYNAME
            return string.IsNullOrEmpty(info.szDisplayName) ? null : info.szDisplayName;
        }
        finally { CoTaskMemFree(pidl); }
    }

    static IntPtr DesktopListView()
    {
        static IntPtr In(IntPtr parent)
        {
            var view = FindWindowEx(parent, IntPtr.Zero, "SHELLDLL_DefView", null);
            return view == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(view, IntPtr.Zero, "SysListView32", null);
        }
        var lv = In(FindWindow("Progman", null));
        var worker = IntPtr.Zero;
        while (lv == IntPtr.Zero)                  // with a wallpaper slideshow it sits under a WorkerW
        {
            worker = FindWindowEx(IntPtr.Zero, worker, "WorkerW", null);
            if (worker == IntPtr.Zero) break;
            lv = In(worker);
        }
        return lv;
    }

    static RectI? FindCore()
    {
        var name = BinName();
        var lv = DesktopListView();
        if (name == null || lv == IntPtr.Zero) return null;
        GetWindowThreadProcessId(lv, out uint pid);
        var proc = OpenProcess(PROCESS_ACCESS, false, pid);
        if (proc == IntPtr.Zero) return null;
        const int bufSize = 4096;
        var remote = VirtualAllocEx(proc, IntPtr.Zero, (UIntPtr)bufSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (remote == IntPtr.Zero) { CloseHandle(proc); return null; }
        try
        {
            int itemSize = Marshal.SizeOf<LVITEMW>();
            var textAddr = remote + itemSize;
            int count = (int)SendMessage(lv, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);
            for (int i = 0; i < count; i++)
            {
                var item = new LVITEMW { mask = LVIF_TEXT, iItem = i, iSubItem = 0, pszText = textAddr, cchTextMax = 260 };
                Write(proc, remote, item);
                SendMessage(lv, LVM_GETITEMTEXTW, (IntPtr)i, remote);
                var textBytes = new byte[520];
                ReadProcessMemory(proc, textAddr, textBytes, (UIntPtr)textBytes.Length, out _);
                var text = System.Text.Encoding.Unicode.GetString(textBytes);
                int z = text.IndexOf('\0');
                if (z >= 0) text = text[..z];
                if (text != name) continue;

                Write(proc, remote, new RECT { Left = LVIR_ICON });       // .left selects which part of the item
                if (SendMessage(lv, LVM_GETITEMRECT, (IntPtr)i, remote) == IntPtr.Zero) return null;
                var rc = Read<RECT>(proc, remote);
                var p = new POINT(rc.Left, rc.Top);
                ClientToScreen(lv, ref p);
                return new RectI(p.X, p.Y, rc.Right - rc.Left, rc.Bottom - rc.Top);
            }
            return null;
        }
        finally
        {
            VirtualFreeEx(proc, remote, UIntPtr.Zero, MEM_RELEASE);
            CloseHandle(proc);
        }
    }

    static void Write<T>(IntPtr proc, IntPtr addr, T value) where T : struct
    {
        int n = Marshal.SizeOf<T>();
        var buf = new byte[n];
        var h = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try { Marshal.StructureToPtr(value, h.AddrOfPinnedObject(), false); }
        finally { h.Free(); }
        WriteProcessMemory(proc, addr, buf, (UIntPtr)n, out _);
    }

    static T Read<T>(IntPtr proc, IntPtr addr) where T : struct
    {
        int n = Marshal.SizeOf<T>();
        var buf = new byte[n];
        ReadProcessMemory(proc, addr, buf, (UIntPtr)n, out _);
        var h = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try { return Marshal.PtrToStructure<T>(h.AddrOfPinnedObject()); }
        finally { h.Free(); }
    }
}
