"""Start with Windows: a value in HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run.

That is the standard per-user autostart list (also shown in Task Manager -> Startup); no admin
rights are needed. The registry is the source of truth, so turning it off in Task Manager is
respected too.

Run from source, the entry starts  pythonw.exe "...\\main.py"  (pythonw: no console window).
Packed into an exe (PyInstaller / Nuitka), it starts the exe itself.
Other systems: not supported, the checkbox is disabled.
"""
import os
import re
import sys

SUPPORTED = sys.platform.startswith("win")
RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"
VALUE_NAME = "YunYunDesktop"
MAIN_PY = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "main.py")

if SUPPORTED:
    import winreg


def command():
    """Command line that starts this copy of the program."""
    if getattr(sys, "frozen", False):                      # PyInstaller / Nuitka exe
        return f'"{sys.executable}"'
    exe = sys.executable
    pyw = os.path.join(os.path.dirname(exe), "pythonw.exe")
    if os.path.isfile(pyw):
        exe = pyw
    return f'"{exe}" "{MAIN_PY}"'


def paths_in(cmd):
    """The quoted paths of a command line ('"a" "b"' -> ['a', 'b'])."""
    return re.findall(r'"([^"]+)"', cmd) or cmd.split()


def _read():
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY) as k:
            v, _ = winreg.QueryValueEx(k, VALUE_NAME)
            return v if isinstance(v, str) else None
    except OSError:
        return None


def is_enabled():
    """Is there an autostart entry for the program (wherever it points)?"""
    return SUPPORTED and bool(_read())


def set_enabled(enabled):
    if not SUPPORTED:
        return
    with winreg.CreateKeyEx(winreg.HKEY_CURRENT_USER, RUN_KEY, 0, winreg.KEY_SET_VALUE) as k:
        if enabled:
            winreg.SetValueEx(k, VALUE_NAME, 0, winreg.REG_SZ, command())
        else:
            try:
                winreg.DeleteValue(k, VALUE_NAME)
            except FileNotFoundError:
                pass


def repair_path():
    """At startup: if the entry points to files that no longer exist (the folder was moved or
    Python reinstalled), point it to this copy. An entry for another existing copy is left alone."""
    if not SUPPORTED:
        return
    cur = _read()
    if cur and not all(os.path.exists(p) for p in paths_in(cur)):
        set_enabled(True)
