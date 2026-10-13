"""Application: companion window + tray icon + settings."""
import os
import sys

from PySide6.QtCore import QPoint, QStandardPaths, QTimer
from PySide6.QtGui import QAction, QIcon, QPixmap
from PySide6.QtWidgets import QApplication, QMenu, QMessageBox, QSystemTrayIcon

from . import autostart, config
from .companion import Companion, premul, to_qimage
from .settings_dialog import SettingsDialog
from .sound import Sound

APP_NAME = "YunYunDesktop"

# grab-sound timing (1 s after the grab, then every 5-10 s) lives in companion.py
REACTION_MS = 3000                      # how long a Recycle Bin reaction picture is shown
BIN_VISITS_TO_QUIT = 3                  # after this many visits the program closes


def config_path():
    d = QStandardPaths.writableLocation(QStandardPaths.AppConfigLocation)
    return os.path.join(d, "settings.json")


class App:
    def __init__(self):
        from PIL import Image
        from . import assets
        from .ragdoll import Ragdoll

        self.qt = QApplication.instance() or QApplication(sys.argv)
        self.qt.setApplicationName(APP_NAME)
        self.qt.setQuitOnLastWindowClosed(False)        # closing the settings window must not quit
        self.cfg_path = config_path()
        self.s = config.load(self.cfg_path)
        try:
            autostart.repair_path()
            self.s.autostart = autostart.is_enabled()   # the registry decides (can be changed in Task Manager)
        except OSError:
            pass

        pack = assets.get()                             # everything comes from yunyun/res.dat
        self.sound = Sound(pack)
        self.sound.set_volume(self.s.volume, self.s.muted)
        self.comp = Companion(pack, self.s, Ragdoll)
        self.comp.hide_requested.connect(self.hide_companion)
        self.comp.settings_requested.connect(self.open_settings)
        self.comp.quit_requested.connect(self.quit)
        self.comp.grab_sound_due.connect(self.sound.play_random)
        self.comp.bin_entered.connect(self._bin_entered)
        self.bin_visits = 0
        self.dialog = None

        # tray icon: her face from the rest pose of the rig
        rd = self.comp.rd
        rd.reset(0, 0)
        face = Image.fromarray(rd.render(premultiplied=False)).crop((300, 15, 480, 195)).resize((64, 64), Image.LANCZOS)
        self._icon_img = to_qimage(premul(face))
        icon = QIcon(QPixmap.fromImage(self._icon_img))
        self.qt.setWindowIcon(icon)

        self.tray = None
        if QSystemTrayIcon.isSystemTrayAvailable():
            self.tray = QSystemTrayIcon(icon)
            self.tray.setToolTip("YunYun")
            menu = QMenu()
            self.act_toggle = QAction("Спрятать", menu)
            self.act_toggle.triggered.connect(self.toggle)
            menu.addAction(self.act_toggle)
            menu.addAction("Настройки…", self.open_settings)
            menu.addSeparator()
            menu.addAction("Выход", self.quit)
            self.tray.setContextMenu(menu)
            self._menu = menu
            self.tray.activated.connect(self._tray_click)
            self.tray.show()

        if self.s.start_hidden and self.tray:
            self.hide_companion()
        else:
            self.show_companion()

    # ------------------------------------------------------------------ visibility
    def show_companion(self):
        self.comp.start()
        if self.tray:
            self.act_toggle.setText("Спрятать")

    def hide_companion(self):
        if not self.tray:                      # no tray on this desktop: hiding would lose her
            return
        self.comp.stop()
        self.act_toggle.setText("Показать")

    def toggle(self):
        if self.comp.isVisible():
            self.hide_companion()
        else:
            self.show_companion()

    def _tray_click(self, reason):
        if reason in (QSystemTrayIcon.Trigger, QSystemTrayIcon.DoubleClick):
            self.toggle()

    # ------------------------------------------------------------------ recycle bin
    def _bin_entered(self):
        self.bin_visits += 1
        self.comp.show_reaction(self.bin_visits - 1)    # pictures in order: bin_1, bin_2, bin_3
        self.sound.play_random()
        QTimer.singleShot(REACTION_MS, self._after_reaction)

    def _after_reaction(self):
        self.comp.finish_reaction()                     # top-left corner (also what gets saved)
        if self.bin_visits >= BIN_VISITS_TO_QUIT:
            self.quit()
        else:
            self.save()

    # ------------------------------------------------------------------ settings
    def open_settings(self):
        if self.dialog is None:
            self.dialog = SettingsDialog(self.s)
            self.dialog.applied.connect(self.apply_settings)
        try:
            self.s.autostart = autostart.is_enabled()   # may have been changed in Task Manager meanwhile
        except OSError:
            pass
        self.dialog.load(self.s)
        self.dialog.show()
        self.dialog.raise_()
        self.dialog.activateWindow()

    def apply_settings(self, s):
        reset_pos = s.position is None and self.s.position is not None
        try:
            if s.autostart != autostart.is_enabled():
                autostart.set_enabled(s.autostart)
        except OSError as e:
            s.autostart = not s.autostart
            QMessageBox.warning(None, "YunYun", f"Не удалось изменить автозапуск:\n{e}")
        self.s = s
        self.comp.apply_settings(s)
        self.sound.set_volume(s.volume, s.muted)
        if reset_pos:
            self.comp._place_initial()
        self.save()

    def save(self):
        if self.s.remember_position and not self.comp.dragging and not self.comp.reacting:
            self.s.position = self.comp.resting_position()
        config.save(self.cfg_path, self.s)

    def quit(self):
        self.save()
        if self.tray:
            self.tray.hide()
        self.qt.quit()

    def run(self):
        self.qt.aboutToQuit.connect(self.save)
        return self.qt.exec()
