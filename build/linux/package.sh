#!/usr/bin/env bash
# Builds a portable tarball and an AppImage from a self-contained publish folder.
# Usage: package.sh <version> <rid> <publish-dir> <output-dir>
set -euo pipefail

VERSION="$1"
RID="$2"
PUBLISH="$3"
OUT="$4"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
mkdir -p "$OUT"

# Portable archive
tar -C "$PUBLISH" -czf "$OUT/ExileLens-$VERSION-$RID.tar.gz" .

# AppImage
case "$RID" in
  linux-x64) ARCH=x86_64 ;;
  linux-arm64) ARCH=aarch64 ;;
  *) echo "Unsupported RID $RID" >&2; exit 1 ;;
esac

APPDIR="$(mktemp -d)/ExileLens.AppDir"
mkdir -p "$APPDIR/usr/bin" "$APPDIR/usr/share/icons/hicolor/256x256/apps"
cp -a "$PUBLISH"/. "$APPDIR/usr/bin/"
cp "$ROOT/src/ExileLens.App/Assets/icon.png" "$APPDIR/usr/share/icons/hicolor/256x256/apps/exile-lens.png"
cp "$ROOT/src/ExileLens.App/Assets/icon.png" "$APPDIR/exile-lens.png"

cat > "$APPDIR/exile-lens.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Exile Lens
Comment=Item price checks and map modifier analysis for Path of Exile 1 & 2
Exec=ExileLens
Icon=exile-lens
Categories=Game;Utility;
Terminal=false
EOF

cat > "$APPDIR/AppRun" <<'EOF'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/bin/ExileLens" "$@"
EOF
chmod +x "$APPDIR/AppRun" "$APPDIR/usr/bin/ExileLens"

TOOL="$(mktemp -d)/appimagetool"
curl -fsSL -o "$TOOL" "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-$ARCH.AppImage"
chmod +x "$TOOL"
# --appimage-extract-and-run avoids needing FUSE on CI runners.
ARCH="$ARCH" "$TOOL" --appimage-extract-and-run "$APPDIR" "$OUT/ExileLens-$VERSION-$RID.AppImage"
