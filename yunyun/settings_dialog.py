"""Settings window (skeleton). Add new options here and in config.Settings."""
from dataclasses import replace

from PySide6.QtCore import Qt, Signal
from PySide6.QtWidgets import (QCheckBox, QComboBox, QDialog, QDialogButtonBox, QFormLayout,
                               QHBoxLayout, QLabel, QPushButton, QSlider, QSpinBox, QTabWidget,
                               QVBoxLayout, QWidget)

from .config import LAYER_DESKTOP, LAYER_NORMAL, LAYER_TOP, Settings
from . import autostart

LAYER_NAMES = [(LAYER_DESKTOP, "Только на рабочем столе (под окнами)"),
               (LAYER_NORMAL, "Как обычное окно"),
               (LAYER_TOP, "Поверх всех окон")]


class SettingsDialog(QDialog):
    applied = Signal(object)          # emits a new Settings when the user presses OK / Apply

    def __init__(self, settings: Settings, parent=None):
        super().__init__(parent, Qt.WindowStaysOnTopHint)
        self.setWindowTitle("YunYun — настройки")
        self.setMinimumWidth(420)
        self._base = settings
        tabs = QTabWidget()
        tabs.addTab(self._tab_look(), "Внешний вид")
        tabs.addTab(self._tab_behaviour(), "Поведение")
        tabs.addTab(self._tab_sound(), "Звук")
        tabs.addTab(self._tab_system(), "Система")
        buttons = QDialogButtonBox(QDialogButtonBox.Ok | QDialogButtonBox.Apply | QDialogButtonBox.Cancel)
        buttons.button(QDialogButtonBox.Ok).setText("ОК")
        buttons.button(QDialogButtonBox.Apply).setText("Применить")
        buttons.button(QDialogButtonBox.Cancel).setText("Отмена")
        buttons.accepted.connect(self._ok)
        buttons.rejected.connect(self.reject)
        buttons.button(QDialogButtonBox.Apply).clicked.connect(self._apply)
        lay = QVBoxLayout(self)
        lay.addWidget(tabs)
        lay.addWidget(buttons)
        self.load(settings)

    # ------------------------------------------------------------------ tabs
    def _tab_look(self):
        w = QWidget(); f = QFormLayout(w)
        self.idle_scale, row = self._percent_slider(20, 100)
        f.addRow("Размер в покое", row)
        self.scale, row = self._percent_slider(30, 100)
        f.addRow("Размер при перетаскивании", row)
        self.fps = QSpinBox(); self.fps.setRange(15, 120); self.fps.setSuffix(" кадр/с")
        f.addRow("Частота анимации", self.fps)
        # TODO: skins, outline / shadow, transparency
        return w

    def _percent_slider(self, lo, hi):
        sl = QSlider(Qt.Horizontal); sl.setRange(lo, hi)
        lbl = QLabel(); lbl.setMinimumWidth(40)
        sl.valueChanged.connect(lambda v: lbl.setText(f"{v}%"))
        sl._lbl = lbl
        row = QHBoxLayout(); row.addWidget(sl); row.addWidget(lbl)
        return sl, row

    def _tab_sound(self):
        w = QWidget(); f = QFormLayout(w)
        self.volume, row = self._percent_slider(0, 100)
        f.addRow("Громкость", row)
        self.muted = QCheckBox("Без звука")
        self.muted.toggled.connect(lambda m: self.volume.setEnabled(not m))
        f.addRow(self.muted)
        note = QLabel("Звуки появятся в следующих версиях.")
        note.setEnabled(False)
        f.addRow(note)
        # TODO: choose voice / sound pack, sounds on grab / release / idle
        return w

    def _tab_behaviour(self):
        w = QWidget(); f = QFormLayout(w)
        self.layer = QComboBox()
        for key, name in LAYER_NAMES:
            self.layer.addItem(name, key)
        f.addRow("Где показывать", self.layer)
        self.remember = QCheckBox("Запоминать, где она сидит")
        f.addRow(self.remember)
        reset = QPushButton("Вернуть в угол экрана")
        reset.clicked.connect(self._reset_position)
        f.addRow(reset)
        # TODO: walking around, reactions to the cursor, idle phrases
        return w

    def _tab_system(self):
        w = QWidget(); f = QFormLayout(w)
        self.start_hidden = QCheckBox("Запускать свёрнутой в трей")
        f.addRow(self.start_hidden)
        self.autostart = QCheckBox("Запускать вместе с Windows")
        if not autostart.SUPPORTED:
            self.autostart.setEnabled(False)
            self.autostart.setToolTip("Только для Windows")
        f.addRow(self.autostart)
        return w

    # ------------------------------------------------------------------ data
    def load(self, s: Settings):
        self._base = s
        for sl, v in ((self.scale, s.scale * 100), (self.idle_scale, s.idle_scale * 100), (self.volume, s.volume)):
            sl.setValue(int(round(v)))
            sl._lbl.setText(f"{sl.value()}%")
        self.muted.setChecked(s.muted)
        self.volume.setEnabled(not s.muted)
        self.fps.setValue(s.fps)
        self.layer.setCurrentIndex(max(0, self.layer.findData(s.layer)))
        self.remember.setChecked(s.remember_position)
        self.start_hidden.setChecked(s.start_hidden)
        self.autostart.setChecked(s.autostart)
        self._position = s.position

    def collect(self) -> Settings:
        return replace(self._base,
                       scale=self.scale.value() / 100.0,
                       idle_scale=self.idle_scale.value() / 100.0,
                       volume=self.volume.value(),
                       muted=self.muted.isChecked(),
                       fps=self.fps.value(),
                       layer=self.layer.currentData(),
                       remember_position=self.remember.isChecked(),
                       start_hidden=self.start_hidden.isChecked(),
                       autostart=self.autostart.isChecked(),
                       position=self._position).clamp()

    def _reset_position(self):
        self._position = None
        self._apply()

    def _apply(self):
        s = self.collect()
        self._base = s
        self.applied.emit(s)

    def _ok(self):
        self._apply()
        self.accept()
