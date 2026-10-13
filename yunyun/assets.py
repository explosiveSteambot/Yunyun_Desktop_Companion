"""Packed resources: every picture, sound and the rig live in one file, yunyun/res.dat.

The file is not an archive any program can open: the contents are compressed and then
mixed with a pseudo-random key stream, so there are no PNG/WAV files to browse or copy and
no recognisable signatures inside. Everything is read straight into memory - nothing is
ever unpacked to disk.

This keeps the resources out of sight, it is not protection against someone determined:
the key is in this (readable) source code, as with any program that has to show its own
pictures.

Layout of res.dat:
    MAGIC (8 bytes) | version u32 | nonce u64 | payload
    payload = zlib(index_len u32 | index json | blobs)  XOR  keystream(KEY, nonce)
    index   = {"path": [offset, size], ...}   offsets relative to the end of the index

Building: python tools/pack_assets.py   (assets_src/ -> yunyun/res.dat)
"""
import hashlib
import io
import json
import os
import struct
import zlib

import numpy as np

MAGIC = b"\x93YYRES\r\n"
VERSION = 1
KEY = b"yunyun-desktop/they-are-just-pictures"
PACK_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "res.dat")


def _keystream(nonce, n):
    seed = int.from_bytes(hashlib.sha256(KEY + struct.pack("<Q", nonce)).digest(), "little")
    words = np.random.PCG64(seed).random_raw((n + 7) // 8)        # raw PCG64 output is stable across numpy versions
    return np.ascontiguousarray(words, dtype="<u8").view(np.uint8)[:n]


def _xor(data, nonce):
    a = np.frombuffer(data, np.uint8)
    return (a ^ _keystream(nonce, a.size)).tobytes()


# ---------------------------------------------------------------- writing (tools/pack_assets.py)
def pack(src_dir, out_path=PACK_PATH):
    """Packs every file under src_dir (paths use '/', relative to src_dir). Returns the file count."""
    files = []
    for root, dirs, names in os.walk(src_dir):
        dirs.sort()
        for n in sorted(names):
            if n.startswith(".") or n.lower().endswith((".txt", ".md")):
                continue
            full = os.path.join(root, n)
            files.append((os.path.relpath(full, src_dir).replace(os.sep, "/"), full))
    index, blobs, off = {}, [], 0
    for rel, full in files:
        data = open(full, "rb").read()
        index[rel] = [off, len(data)]
        blobs.append(data)
        off += len(data)
    idx = json.dumps(index, separators=(",", ":")).encode("utf-8")
    body = zlib.compress(struct.pack("<I", len(idx)) + idx + b"".join(blobs), 9)
    nonce = int.from_bytes(os.urandom(8), "little")
    tmp = out_path + ".tmp"
    with open(tmp, "wb") as f:
        f.write(MAGIC + struct.pack("<IQ", VERSION, nonce) + _xor(body, nonce))
    os.replace(tmp, out_path)
    return len(files)


# ---------------------------------------------------------------- reading
class Pack:
    def __init__(self, path=PACK_PATH):
        raw = open(path, "rb").read()
        if raw[:8] != MAGIC:
            raise ValueError(f"{path}: not a resource pack")
        version, nonce = struct.unpack_from("<IQ", raw, 8)
        if version != VERSION:
            raise ValueError(f"{path}: unsupported version {version}")
        body = zlib.decompress(_xor(raw[20:], nonce))
        n = struct.unpack_from("<I", body)[0]
        self._index = json.loads(body[4:4 + n].decode("utf-8"))
        self._data = memoryview(body)[4 + n:]

    def names(self):
        return sorted(self._index)

    def exists(self, path):
        return path in self._index

    def read(self, path):
        """bytes of one packed file, path like 'rig/body.png'."""
        try:
            off, size = self._index[path]
        except KeyError:
            raise FileNotFoundError(f"resource not found: {path}") from None
        return bytes(self._data[off:off + size])

    def list(self, folder, ext=""):
        """Sorted paths directly inside folder, e.g. list('idle', '.png')."""
        pre = folder.rstrip("/") + "/"
        return [p for p in self.names() if p.startswith(pre) and "/" not in p[len(pre):] and p.lower().endswith(ext)]

    def image(self, path):
        """PIL image (fully loaded, independent of the pack)."""
        from PIL import Image
        im = Image.open(io.BytesIO(self.read(path)))
        im.load()
        return im

    def reader(self, folder):
        """read(name) for files inside one folder - what Ragdoll expects."""
        pre = folder.rstrip("/") + "/"
        return lambda name: self.read(pre + name)


_pack = None


def get():
    """The application's pack (loaded once)."""
    global _pack
    if _pack is None:
        _pack = Pack()
    return _pack
