"""Settings window (skeleton). Add new options here and in config.Settings."""
import os
import sys
from dataclasses import replace

from PySide6.QtCore import Qt, Signal
from PySide6.QtGui import QFontDatabase
from PySide6.QtWidgets import (QCheckBox, QComboBox, QDialog, QDialogButtonBox, QFormLayout,
                               QHBoxLayout, QLabel, QPlainTextEdit, QPushButton, QSlider, QSpinBox,
                               QTabWidget, QVBoxLayout, QWidget)

from .config import LAYER_DESKTOP, LAYER_NORMAL, LAYER_TOP, Settings
from . import __version__, autostart, i18n
from .i18n import tr

STEAM_URL = "https://store.steampowered.com/app/2914150/Yunyun_Syndrome_Rhythm_Psychosis/"
AUTHOR_URL = "https://github.com/explosiveSteambot"
REPO_URL = "https://github.com/explosiveSteambot/Yunyun_Desktop_Companion"


def license_text():
    """The license file: next to main.py when run from source, in the bundle when frozen."""
    root = getattr(sys, "_MEIPASS", os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    try:
        with open(os.path.join(root, "license"), encoding="utf-8") as f:
            return f.read()
    except OSError:
        return tr("about.no_license") + "\n" + REPO_URL


LAYERS = [(LAYER_DESKTOP, "layer.desktop"), (LAYER_NORMAL, "layer.normal"), (LAYER_TOP, "layer.top")]


class SettingsDialog(QDialog):
    applied = Signal(object)          # emits a new Settings when the user presses OK / Apply

    def __init__(self, settings: Settings, parent=None):
        super().__init__(parent, Qt.WindowStaysOnTopHint)
        self.setWindowTitle(tr("settings.title"))
        self.setMinimumWidth(420)
        self._base = settings
        self.tabs = tabs = QTabWidget()
        tabs.setUsesScrollButtons(False)       # the window grows so that every tab is visible
        tabs.addTab(self._tab_look(), tr("tab.look"))
        tabs.addTab(self._tab_behaviour(), tr("tab.behavior"))
        tabs.addTab(self._tab_sound(), tr("tab.sound"))
        tabs.addTab(self._tab_system(), tr("tab.system"))
        tabs.addTab(self._tab_about(), tr("tab.about"))
        buttons = QDialogButtonBox(QDialogButtonBox.Ok | QDialogButtonBox.Apply | QDialogButtonBox.Cancel)
        buttons.button(QDialogButtonBox.Ok).setText(tr("btn.ok"))
        buttons.button(QDialogButtonBox.Apply).setText(tr("btn.apply"))
        buttons.button(QDialogButtonBox.Cancel).setText(tr("btn.cancel"))
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
        f.addRow(tr("look.idle_scale"), row)
        self.scale, row = self._percent_slider(30, 100)
        f.addRow(tr("look.drag_scale"), row)
        self.fps = QSpinBox(); self.fps.setRange(15, 120); self.fps.setSuffix(" " + tr("look.fps_unit"))
        f.addRow(tr("look.fps"), self.fps)
        self.mirror_idle = QCheckBox(tr("look.mirror_idle"))
        f.addRow(self.mirror_idle)
        self.mirror_drag = QCheckBox(tr("look.mirror_drag"))
        f.addRow(self.mirror_drag)
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
        f.addRow(tr("sound.volume"), row)
        self.muted = QCheckBox(tr("sound.mute"))
        self.muted.toggled.connect(lambda m: self.volume.setEnabled(not m))
        f.addRow(self.muted)
        note = QLabel(tr("sound.note"))
        note.setEnabled(False)
        f.addRow(note)
        # TODO: choose voice / sound pack, sounds on grab / release / idle
        return w

    def _tab_behaviour(self):
        w = QWidget(); f = QFormLayout(w)
        self.layer = QComboBox()
        for key, text in LAYERS:
            self.layer.addItem(tr(text), key)
        f.addRow(tr("behavior.where"), self.layer)
        self.remember = QCheckBox(tr("behavior.remember"))
        f.addRow(self.remember)
        reset = QPushButton(tr("behavior.reset"))
        reset.clicked.connect(self._reset_position)
        f.addRow(reset)
        # TODO: walking around, reactions to the cursor, idle phrases
        return w

    def _tab_system(self):
        w = QWidget(); f = QFormLayout(w)
        self.language = QComboBox()
        self.language.addItem(tr("system.language_auto"), i18n.AUTO)
        for code in i18n.LANGUAGES:
            self.language.addItem(i18n.LANGUAGE_NAMES[code], code)
        f.addRow(tr("system.language"), self.language)
        self.start_hidden = QCheckBox(tr("system.start_hidden"))
        f.addRow(self.start_hidden)
        self.autostart = QCheckBox(tr("system.autostart"))
        if not autostart.SUPPORTED:
            self.autostart.setEnabled(False)
            self.autostart.setToolTip(tr("system.windows_only"))
        f.addRow(self.autostart)
        return w

    def _tab_about(self):
        w = QWidget(); lay = QVBoxLayout(w)
        info = QLabel(
            f"<b>YunYun Desktop Companion</b> {__version__}<br><br>"
            + tr("about.rights").replace("\n", " ") + "<br><br>"
            f'{tr("about.steam")} <a href="{STEAM_URL}">YunYun Syndrome</a><br>'
            f'{tr("about.author")} <a href="{AUTHOR_URL}">explosiveSteambot</a> {tr("about.on_github")}<br>'
            f'{tr("about.source")} <a href="{REPO_URL}">Yunyun_Desktop_Companion</a>')
        info.setWordWrap(True)
        info.setOpenExternalLinks(True)
        info.setTextInteractionFlags(Qt.TextBrowserInteraction)
        lay.addWidget(info)
        lay.addWidget(QLabel(tr("about.license")))
        lic = QPlainTextEdit(license_text())
        lic.setReadOnly(True)
        lic.setLineWrapMode(QPlainTextEdit.NoWrap)
        lic.setFont(QFontDatabase.systemFont(QFontDatabase.FixedFont))   # the "=====" rules line up
        lic.setMinimumHeight(180)
        lay.addWidget(lic, 1)
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
        self.language.setCurrentIndex(max(0, self.language.findData(s.language)))
        self.mirror_idle.setChecked(s.mirror_idle)
        self.mirror_drag.setChecked(s.mirror_drag)
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
                       language=self.language.currentData(),
                       mirror_idle=self.mirror_idle.isChecked(),
                       mirror_drag=self.mirror_drag.isChecked(),
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
