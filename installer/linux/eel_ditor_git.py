import os
from pathlib import Path
import subprocess

from gi.repository import GLib, GObject, Nautilus


class EelDitorGitMenu(GObject.GObject, Nautilus.MenuProvider):
    def get_file_items(self, *args):
        # Nautilus versions before 43 also pass the file manager window.
        files = args[-1]
        if len(files) != 1 or not files[0].is_directory():
            return []
        path = files[0].get_location().get_path()
        if path is None:
            return []
        marker = Path(path) / ".git"
        if not (marker.is_dir() or marker.is_file()):
            return []
        data = Path(os.environ.get("XDG_DATA_HOME") or Path.home() / ".local/share")
        executable = data / "eel-ditor/eel-ditor"
        if not executable.is_file():
            return []

        polish = any(name == "pl" or name.startswith("pl_") for name in GLib.get_language_names())
        label = "Otwórz w Eel-ditor (Git)" if polish else "Open in Eel-ditor (Git)"
        item = Nautilus.MenuItem(name="EelDitor::OpenGit", label=label, tip="", icon="eel-ditor")
        item.connect("activate", self.open_repository, str(executable), path)
        return [item]

    @staticmethod
    def open_repository(_item, executable, path):
        subprocess.Popen([executable, f"--git={path}"], start_new_session=True)
