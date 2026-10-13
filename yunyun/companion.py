"""The companion window: resting animation + ragdoll while dragged."""
import random
import time

import numpy as np
from PIL import Image
from PySide6.QtCore import QPoint, QRect, Qt, QTimer, Signal
from PySide6.QtGui import QCursor, QGuiApplication, QImage, QPainter
from PySide6.QtWidgets import QMenu, QWidget

from . import geometry, recyclebin, winlayer
from .config import LAYER_DESKTOP, Settings

GRAB_FIRST_SOUND = 1.0                  # s after the grab
GRAB_SOUND_EVERY = (5.0, 10.0)          # s between the following ones while she is held


def to_qimage(rgba_premul):
    h, w = rgba_premul.shape[:2]
    img = QImage(rgba_premul.data, w, h, 4 * w, QImage.Format_RGBA8888_Premultiplied)
    img._keep = rgba_premul                          # keep the numpy buffer alive
    return img


def premul(im):
    a = np.asarray(im.convert("RGBA")).astype(np.float32) / 255.0
    a[..., :3] *= a[..., 3:4]
    return np.ascontiguousarray((a * 255 + 0.5).astype(np.uint8))


class Companion(QWidget):
    hide_requested = Signal()            # "minimise to tray" from her own context menu
    settings_requested = Signal()
    quit_requested = Signal()
    grab_started = Signal()
    grab_ended = Signal()
    bin_entered = Signal()              # the dragged character reached the Recycle Bin icon
    grab_sound_due = Signal()           # time to play a "being held" sound

    def __init__(self, pack, settings: Settings, ragdoll_cls):
        super().__init__(None, winlayer.initial_flags(settings.layer))
        self.setAttribute(Qt.WA_TranslucentBackground)
        self.setAttribute(Qt.WA_ShowWithoutActivating)   # never steal focus from the user's work
        self.setFocusPolicy(Qt.NoFocus)
        self.pack = pack                 # packed resources (yunyun/assets.py)
        self.s = settings
        self.rd = ragdoll_cls(pack.reader("rig"))
        self.grab = np.array(self.rd.grab_point, float)
        self._load_idle()
        self.dragging = False
        # Grab-sound timing is checked in tick(), not with a separate QTimer: on Windows timer
        # messages are only produced when the event queue is empty, and while she is dragged it
        # never is (mouse moves + rendering every frame), so such a timer simply never fired.
        self.next_grab_sound = None
        self.reacting = False            # showing a reaction picture (Recycle Bin)
        self.bin = None                  # Recycle Bin rect in Qt pixels, refreshed on every grab
        self._load_reactions()
        self.frame = None
        self.t0 = self.last = time.perf_counter()
        self.timer = QTimer(self)
        self.timer.timeout.connect(self.tick)
        self.layer_timer = QTimer(self)                  # desktop mode on Windows: stay under other windows
        self.layer_timer.timeout.connect(self.keep_layer)
        self.layer_timer.start(700)
        self._place_initial()

    # ------------------------------------------------------------------ setup
    def _load_idle(self):
        frames = [self.pack.image(p) for p in self.pack.list("idle", ".png")]
        sc = self.s.idle_scale
        size = (max(1, int(frames[0].width * sc)), max(1, int(frames[0].height * sc)))
        self.idle = [to_qimage(premul(f.convert("RGBA").resize(size, Image.LANCZOS))) for f in frames]
        # opaque part of the resting pose: only it must stay on the work area
        union = None
        for f in frames:
            bb = f.getchannel("A").getbbox()
            if bb:
                union = bb if union is None else (min(union[0], bb[0]), min(union[1], bb[1]),
                                                   max(union[2], bb[2]), max(union[3], bb[3]))
        x0, y0, x1, y1 = union or (0, 0, frames[0].width, frames[0].height)
        self.idle_visible = (int(x0 * sc), int(y0 * sc), int((x1 - x0) * sc), int((y1 - y0) * sc))
        self.idle_ms = 30

    def _load_reactions(self):
        files = self.pack.list("reactions", ".png")
        sc = self.s.idle_scale
        self.reactions = []
        for f in files:
            im = self.pack.image(f).convert("RGBA")
            im = im.resize((max(1, int(im.width * sc)), max(1, int(im.height * sc))), Image.LANCZOS)
            self.reactions.append(to_qimage(premul(im)))

    def _place_initial(self):
        img = self.idle[0]
        self.resize(img.width(), img.height())
        if self.s.remember_position and self.s.position:
            self.move(QPoint(*self.s.position))
        else:
            a = QGuiApplication.primaryScreen().availableGeometry()
            self.move(QPoint(a.right() - img.width() - 40, a.bottom() - img.height() - 40))
        self.clamp_to_work_area()

    # ------------------------------------------------------------------ public
    def start(self):
        self.show()
        winlayer.apply(self, self.s.layer)
        self.timer.start(int(1000 / self.s.fps))

    def stop(self):
        self.timer.stop()
        self.hide()

    def apply_settings(self, s: Settings):
        old = self.s
        self.s = s
        if s.idle_scale != old.idle_scale:
            center = self.geometry().center()
            self._load_idle()
            self._load_reactions()
            img = self.idle[0]
            self.resize(img.width(), img.height())
            self.move(center - QPoint(img.width() // 2, img.height() // 2))
            self.clamp_to_work_area()
        if self.isVisible():
            self.timer.start(int(1000 / s.fps))
            winlayer.apply(self, s.layer)

    def resting_position(self):
        return [self.x(), self.y()]

    # ------------------------------------------------------------------ geometry
    def work_areas(self):
        return [tuple(sc.availableGeometry().getRect()) for sc in QGuiApplication.screens()]

    def clamp_to_work_area(self):
        """Keep the resting character inside the desktop work area (not over the taskbar)."""
        g = self.geometry()
        vx, vy, vw, vh = self.idle_visible
        cx, cy = g.x() + vx + vw // 2, g.y() + vy + vh // 2
        area = geometry.pick_area(self.work_areas(), cx, cy)
        x, y, _, _ = geometry.clamp_rect(tuple(g.getRect()), area, self.idle_visible)
        self.move(int(x), int(y))

    def nativeEvent(self, event_type, message):
        if self.s.layer == LAYER_DESKTOP:
            winlayer.keep_bottom_on(event_type, message)
        return super().nativeEvent(event_type, message)

    def keep_layer(self):
        if self.isVisible() and self.s.layer == LAYER_DESKTOP:
            winlayer.reassert_bottom(self)

    # ------------------------------------------------------------------ recycle bin
    def _bin_rect(self):
        """Recycle Bin icon in Qt (logical) pixels, or None.

        settings.extra["bin_rect"] = [x, y, w, h] overrides it (handy for testing / other OS)."""
        manual = self.s.extra.get("bin_rect") if isinstance(self.s.extra, dict) else None
        if manual and len(manual) == 4:
            return tuple(int(v) for v in manual)
        r = recyclebin.recycle_bin_rect()
        if not r:
            return None
        # Win32 gives physical pixels, Qt works in logical ones (display scaling 125 %, 150 % ...)
        dpr = QGuiApplication.primaryScreen().devicePixelRatio() or 1.0
        x, y, w, h = r
        return (int(x / dpr), int(y / dpr), max(1, int(w / dpr)), max(1, int(h / dpr)))

    def _over_bin(self, p):
        if not self.bin:
            return False
        x, y, w, h = self.bin
        m = 6                                            # a little tolerance around the icon
        return x - m <= p.x() < x + w + m and y - m <= p.y() < y + h + m

    def show_reaction(self, i):
        """Replace the character with reaction picture i, centred on the Recycle Bin."""
        self.dragging = False
        self.reacting = True
        img = self.reactions[min(i, len(self.reactions) - 1)] if self.reactions else self.idle[0]
        self.frame = img
        self.resize(img.width(), img.height())
        if self.bin:
            x, y, w, h = self.bin
            r = (x + w // 2 - img.width() // 2, y + h // 2 - img.height() // 2, img.width(), img.height())
            area = geometry.pick_area(self.work_areas(), x + w // 2, y + h // 2)
            nx, ny, _, _ = geometry.clamp_rect(r, area)   # whole picture stays on screen
            self.move(int(nx), int(ny))
        winlayer.apply(self, self.s.layer)               # the chosen layer (desktop: under windows)
        self.update()

    def finish_reaction(self):
        """Back to the resting animation in the top-left corner of the work area."""
        self.reacting = False
        img = self.idle[0]
        self.resize(img.width(), img.height())
        g = self.geometry()
        area = geometry.pick_area(self.work_areas(), g.center().x(), g.center().y())
        vx, vy, _, _ = self.idle_visible
        self.move(int(area[0] - vx), int(area[1] - vy))  # visible part touches the corner
        winlayer.apply(self, self.s.layer)

    # ------------------------------------------------------------------ drag
    def window_pos_for(self, cursor):
        g = self.grab * self.s.scale
        return QPoint(int(cursor.x() - g[0]), int(cursor.y() - g[1]))

    def start_drag(self, cursor):
        if self.reacting:
            return
        self.bin = self._bin_rect()                      # icons may have moved since last time
        self.dragging = True
        sc = self.s.scale
        self.resize(int(self.rd.W * sc), int(self.rd.H * sc))
        pos = self.window_pos_for(cursor)
        self.move(pos)
        self.rd.reset(pos.x() / sc, pos.y() / sc)        # physics works in rig pixels
        winlayer.apply(self, self.s.layer)               # the chosen layer, also while dragged
        self.next_grab_sound = time.perf_counter() + GRAB_FIRST_SOUND
        self.grab_started.emit()

    def end_drag(self):
        self.dragging = False
        self.next_grab_sound = None
        self.grab_ended.emit()
        center = self.geometry().center()
        img = self.idle[0]
        self.resize(img.width(), img.height())
        self.move(center - QPoint(img.width() // 2, img.height() // 2))
        self.clamp_to_work_area()
        winlayer.apply(self, self.s.layer)

    # ------------------------------------------------------------------ events
    def mousePressEvent(self, e):
        if e.button() == Qt.LeftButton:
            self.start_drag(e.globalPosition().toPoint())

    def mouseReleaseEvent(self, e):
        if e.button() == Qt.LeftButton and self.dragging:
            self.end_drag()

    def contextMenuEvent(self, e):
        if self.reacting:
            return
        m = QMenu(self)
        m.addAction("Спрятать в трей", self.hide_requested.emit)
        m.addAction("Настройки…", self.settings_requested.emit)
        m.addSeparator()
        m.addAction("Выход", self.quit_requested.emit)
        m.exec(e.globalPos())

    def tick(self):
        now = time.perf_counter()
        dt, self.last = now - self.last, now
        if self.dragging:
            if not (QGuiApplication.mouseButtons() & Qt.LeftButton):   # release happened outside the window
                self.end_drag()
                return
            sc = self.s.scale
            pos = self.window_pos_for(QCursor.pos())
            self.move(pos)
            self.rd.step(dt, pos.x() / sc, pos.y() / sc)
            self.frame = to_qimage(self.rd.render(premultiplied=True))
            if self._over_bin(QCursor.pos()):
                self.dragging = False
                self.next_grab_sound = None
                self.grab_ended.emit()
                self.bin_entered.emit()                  # App decides what happens next
                return
            if self.next_grab_sound is not None and now >= self.next_grab_sound:
                self.next_grab_sound = now + random.uniform(*GRAB_SOUND_EVERY)
                self.grab_sound_due.emit()
        elif self.reacting:
            pass                                         # frame is the reaction picture
        else:
            k = int((now - self.t0) * 1000 / self.idle_ms) % len(self.idle)
            self.frame = self.idle[k]
        self.update()

    def paintEvent(self, e):
        if self.frame is None:
            return
        p = QPainter(self)
        p.setRenderHint(QPainter.SmoothPixmapTransform)
        p.drawImage(self.rect(), self.frame)
        p.end()
