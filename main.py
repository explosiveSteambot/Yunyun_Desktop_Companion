"""
YunYun desktop companion.

    pip install PySide6 numpy opencv-python pillow
    python main.py

Pictures, sounds and the rig are read from yunyun/res.dat (see yunyun/assets.py);
after changing anything in assets_src/ run  python tools/pack_assets.py

* lives on the desktop (under your windows), never over the taskbar
* drag her with the left mouse button - ragdoll physics
* tray icon: click to hide / show, menu - settings, quit
* right click on her: hide to tray, settings, quit
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

from yunyun.app import App

if __name__ == "__main__":
    sys.exit(App().run())
