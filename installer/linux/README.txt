Eel-ditor for Linux (x64)

Run without installing:
    ./eel-ditor [files...]

Install for the current user (no root; menu entry, icon, the command eel-ditor in ~/.local/bin):
    ./install.sh
Remove:
    ./install.sh --uninstall

The program is self-contained: no .NET runtime is needed. It needs an X11 desktop (or XWayland), fontconfig and the
ICU library (libicu), which common distributions have. Settings are in ~/.config/Eel-ditor.
Git features use the git command; PDF export uses Chrome or Chromium (if installed).

Open a working tree in the Git tab without opening a file:
    ./eel-ditor --git="/path/to/repository"
The installer also provides "Open in Eel-ditor (Git)" in GNOME Files (Nautilus) for folders containing
a .git directory or worktree file. Install the distribution's nautilus-python extension loader and
restart Nautilus to enable it.
