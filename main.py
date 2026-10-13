"""
YunYun desktop companion.

    pip install -r requirements.txt
    python main.py

Release build (Windows): build_release.bat  ->  dist\\YunYun.exe

Pictures, sounds and the rig are read from yunyun/res.dat (see yunyun/assets.py);
after changing anything in assets_src/ run  python tools/pack_assets.py

* lives on the desktop (under your windows), never over the taskbar
* drag her with the left mouse button - ragdoll physics
* tray icon: click to hide / show, menu - settings, quit
* right click on her: hide to tray, settings, quit
"""
import datetime
import os
import sys
import traceback

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

FROZEN = getattr(sys, "frozen", False)          # running from the PyInstaller exe
LOG_DIR = os.path.join(os.environ.get("APPDATA") or os.path.expanduser("~"), "YunYunDesktop")
LOG_PATH = os.path.join(LOG_DIR, "error.log")


def _log_stream():
    os.makedirs(LOG_DIR, exist_ok=True)
    return open(LOG_PATH, "a", encoding="utf-8", buffering=1)


def _report(exc_type, exc, tb):
    """Uncaught error: write it to error.log (an exe has no console) and tell the user."""
    text = "".join(traceback.format_exception(exc_type, exc, tb))
    try:
        with _log_stream() as f:
            f.write(f"\n=== {datetime.datetime.now():%Y-%m-%d %H:%M:%S} ===\n{text}")
    except OSError:
        pass
    if FROZEN and sys.platform.startswith("win"):
        import ctypes
        ctypes.windll.user32.MessageBoxW(None, f"Что-то пошло не так:\n{exc}\n\nПодробности: {LOG_PATH}",
                                         "YunYun", 0x10)           # MB_ICONERROR
    else:
        sys.__excepthook__(exc_type, exc, tb)


def _single_instance():
    """Returns a lock that must be kept alive, or None if YunYun is already running."""
    from PySide6.QtCore import QDir, QLockFile
    lock = QLockFile(os.path.join(QDir.tempPath(), "yunyun-desktop.lock"))   # stale locks (after a crash) are detected
    return lock if lock.tryLock(0) else None


if __name__ == "__main__":
    if FROZEN and sys.stderr is None:            # windowed exe: no console, keep warnings in the log
        sys.stdout = sys.stderr = _log_stream()
    sys.excepthook = _report                     # also catches errors in Qt slots
    lock = _single_instance()
    if lock is None:
        sys.exit(0)                              # already running (e.g. autostart + a manual launch)
    try:
        from yunyun.app import App
        code = App().run()
    except Exception:
        _report(*sys.exc_info())
        code = 1
    lock.unlock()
    sys.exit(code)
