#!/bin/sh
# Installs Eel-ditor for the current user (no root needed):
#   the program in ~/.local/share/eel-ditor, the command eel-ditor in ~/.local/bin,
#   a menu entry and an icon. "./install.sh --uninstall" removes them again.
# Eel-ditor also runs without installing: ./eel-ditor [files...]
set -e

here=$(cd "$(dirname "$0")" && pwd)
data="${XDG_DATA_HOME:-$HOME/.local/share}"
appdir="$data/eel-ditor"
bindir="$HOME/.local/bin"
desktop="$data/applications/eel-ditor.desktop"
icon="$data/icons/hicolor/256x256/apps/eel-ditor.png"
nautilus="$data/nautilus-python/extensions/eel_ditor_git.py"

refresh() {
    command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$data/applications" >/dev/null 2>&1 || true
    command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t "$data/icons/hicolor" >/dev/null 2>&1 || true
}

if [ "$1" = "--uninstall" ]; then
    rm -rf "$appdir"
    rm -f "$bindir/eel-ditor" "$desktop" "$icon" "$nautilus"
    refresh
    echo "Eel-ditor removed. Settings stay in ${XDG_CONFIG_HOME:-$HOME/.config}/Eel-ditor."
    exit 0
fi

mkdir -p "$appdir" "$bindir" "$(dirname "$desktop")" "$(dirname "$icon")"
cp "$here/eel-ditor" "$appdir/eel-ditor"
chmod 755 "$appdir/eel-ditor"
ln -sf "$appdir/eel-ditor" "$bindir/eel-ditor"
cp "$here/eel-ditor.png" "$icon"
sed "s|@EXEC@|$appdir/eel-ditor|" "$here/eel-ditor.desktop" > "$desktop"
mkdir -p "$(dirname "$nautilus")"
cp "$here/eel_ditor_git.py" "$nautilus"
refresh

echo "Eel-ditor installed: $appdir"
case ":$PATH:" in
    *":$bindir:"*) echo "Start it from the menu or with: eel-ditor [files...]" ;;
    *) echo "Start it from the menu, or add $bindir to PATH to run: eel-ditor [files...]" ;;
esac
