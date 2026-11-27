#!/usr/bin/env bash
# Builds ExileLens.app, a zip and a DMG from a self-contained publish folder.
# Usage: package.sh <version> <rid> <publish-dir> <output-dir>
# The bundle is ad-hoc signed only; Gatekeeper will ask the user to confirm on first launch (see docs/PLATFORM-SUPPORT.md).
set -euo pipefail

VERSION="$1"
RID="$2"
PUBLISH="$3"
OUT="$4"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
mkdir -p "$OUT"

WORK="$(mktemp -d)"
APP="$WORK/Exile Lens.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -a "$PUBLISH"/. "$APP/Contents/MacOS/"

# Icon: build an .icns from the PNG.
ICONSET="$WORK/icon.iconset"
mkdir -p "$ICONSET"
for size in 16 32 64 128 256; do
  sips -z $size $size "$ROOT/src/ExileLens.App/Assets/icon.png" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/ExileLens.icns"

cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Exile Lens</string>
  <key>CFBundleDisplayName</key><string>Exile Lens</string>
  <key>CFBundleIdentifier</key><string>net.exilelens.app</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleExecutable</key><string>ExileLens</string>
  <key>CFBundleIconFile</key><string>ExileLens.icns</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>LSUIElement</key><false/>
  <key>NSAppleEventsUsageDescription</key><string>Exile Lens sends the item copy keystroke to the game when you press its shortcut.</string>
</dict>
</plist>
EOF

chmod +x "$APP/Contents/MacOS/ExileLens"
codesign --force --deep --sign - "$APP"

ditto -c -k --sequesterRsrc --keepParent "$APP" "$OUT/ExileLens-$VERSION-$RID.zip"
hdiutil create -volname "Exile Lens" -srcfolder "$APP" -ov -format UDZO "$OUT/ExileLens-$VERSION-$RID.dmg"
