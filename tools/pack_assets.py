"""Builds yunyun/res.dat from the assets_src/ folder.

    python tools/pack_assets.py

Run it after changing anything in assets_src/ (layers, rig.json, idle frames, reaction
pictures, sounds). The program itself only reads res.dat, so assets_src/ does not need to
be shipped.
"""
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, ROOT)

from yunyun import assets  # noqa: E402

if __name__ == "__main__":
    src = os.path.join(ROOT, "assets_src")
    n = assets.pack(src)
    size = os.path.getsize(assets.PACK_PATH)
    p = assets.Pack()                                       # read it back to be sure
    assert len(p.names()) == n
    print(f"{n} files -> {os.path.relpath(assets.PACK_PATH, ROOT)} ({size / 1e6:.1f} MB)")
