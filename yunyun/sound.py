"""Short sound effects: every sounds/*.wav from the resource pack, one random pool.

Windows: played straight from memory with the system PlaySound (winsound) - nothing is written
to disk. PlaySound has no volume control, so the volume is applied by scaling the 16-bit
samples (a scaled copy of every clip is rebuilt when the volume changes). winsound cannot play
from memory asynchronously, so each sound is played on a short-lived background thread.

Other systems: QSoundEffect needs files, so the clips are written to a private temporary
folder that is removed when the program exits.
"""
import atexit
import io
import random
import shutil
import sys
import tempfile
import threading
import wave

import numpy as np

IS_WIN = sys.platform.startswith("win")
if IS_WIN:
    import winsound


def scaled_wav(data, gain):
    """The wav with its samples multiplied by gain (16-bit PCM; other formats are returned as is)."""
    try:
        with wave.open(io.BytesIO(data)) as w:
            params = w.getparams()
            frames = w.readframes(params.nframes)
    except (wave.Error, EOFError):
        return data
    if params.sampwidth != 2:
        return data
    a = np.frombuffer(frames, "<i2").astype(np.float32) * gain
    out = io.BytesIO()
    with wave.open(out, "wb") as w:
        w.setparams(params)
        w.writeframes(np.clip(np.round(a), -32768, 32767).astype("<i2").tobytes())
    return out.getvalue()


class Sound:
    def __init__(self, pack):
        self.clips = [pack.read(p) for p in pack.list("sounds", ".wav")]
        self.gain = 0.7                 # 0..1
        self._last = None
        self._scaled = []
        self._effects = []
        self._tmp = None
        if IS_WIN:
            self._rebuild()
        else:
            self._init_qt()

    # ------------------------------------------------------------------ other systems
    def _init_qt(self):
        try:
            from PySide6.QtCore import QUrl
            from PySide6.QtMultimedia import QSoundEffect
        except ImportError:             # no QtMultimedia: the program works, just silently
            return
        self._tmp = tempfile.mkdtemp(prefix="yy")
        atexit.register(shutil.rmtree, self._tmp, True)
        for i, data in enumerate(self.clips):
            path = f"{self._tmp}/{i}.wav"
            with open(path, "wb") as f:
                f.write(data)
            fx = QSoundEffect()
            fx.setSource(QUrl.fromLocalFile(path))
            fx.setVolume(self.gain)
            self._effects.append(fx)

    # ------------------------------------------------------------------ public
    def set_volume(self, volume, muted=False):
        """volume: 0..100 from the settings."""
        gain = 0.0 if muted else max(0, min(100, volume)) / 100.0
        if abs(gain - self.gain) < 1e-4:
            return
        self.gain = gain
        if IS_WIN:
            self._rebuild()
        for fx in self._effects:
            fx.setVolume(gain)

    def _rebuild(self):
        self._scaled = [scaled_wav(d, self.gain) for d in self.clips]

    def play_random(self):
        """Play one random sound, never the same one twice in a row. Returns its index or None."""
        n = len(self._scaled) if IS_WIN else len(self._effects)
        if n == 0 or self.gain <= 0:
            return None
        choices = [i for i in range(n) if i != self._last] or [0]
        i = random.choice(choices)
        self._last = i
        if IS_WIN:
            data = self._scaled[i]
            threading.Thread(target=self._play_win, args=(data,), daemon=True).start()
        else:
            self._effects[i].play()
        return i

    @staticmethod
    def _play_win(data):
        try:
            winsound.PlaySound(data, winsound.SND_MEMORY | winsound.SND_NODEFAULT)
        except RuntimeError:
            pass                        # no audio device etc.: stay silent rather than crash
