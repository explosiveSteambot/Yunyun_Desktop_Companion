"""Settings: a dataclass persisted as JSON. No Qt here, so it can be tested on its own."""
from __future__ import annotations

import json
import os
from dataclasses import asdict, dataclass, field, fields

# where the companion lives relative to other windows
LAYER_DESKTOP = "desktop"   # on the desktop, under all ordinary windows
LAYER_NORMAL = "normal"     # like a normal window
LAYER_TOP = "top"           # always on top
LAYERS = (LAYER_DESKTOP, LAYER_NORMAL, LAYER_TOP)


@dataclass
class Settings:
    scale: float = 0.6               # size of the character while dragged (0.3 .. 1.0)
    idle_scale: float = 0.6          # size of the resting (idle) animation (0.2 .. 1.0)
    volume: int = 70                 # sound volume, 0..100 (sound is not added yet)
    muted: bool = False
    fps: int = 60                    # animation / physics rate
    layer: str = LAYER_DESKTOP       # see LAYERS
    start_hidden: bool = False       # start minimised to the tray
    remember_position: bool = True
    position: list | None = None     # last resting position [x, y] (window top-left)
    autostart: bool = False          # mirrors the registry entry (autostart.py)
    extra: dict = field(default_factory=dict)   # room for future options

    def clamp(self) -> "Settings":
        self.scale = min(1.0, max(0.3, float(self.scale)))
        self.idle_scale = min(1.0, max(0.2, float(self.idle_scale)))
        self.volume = int(min(100, max(0, int(self.volume))))
        self.muted = bool(self.muted)
        self.fps = int(min(120, max(15, int(self.fps))))
        if self.layer not in LAYERS:
            self.layer = LAYER_DESKTOP
        if self.position is not None and (len(self.position) != 2):
            self.position = None
        return self


def load(path: str) -> Settings:
    """Read settings; unknown keys are ignored, missing ones get defaults, bad files are reset."""
    try:
        with open(path, encoding="utf-8") as f:
            raw = json.load(f)
        known = {f.name for f in fields(Settings)}
        return Settings(**{k: v for k, v in raw.items() if k in known}).clamp()
    except (OSError, ValueError, TypeError):
        return Settings()


def save(path: str, s: Settings) -> None:
    os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(asdict(s.clamp()), f, ensure_ascii=False, indent=2)
    os.replace(tmp, path)            # atomic: never leaves a half-written file
