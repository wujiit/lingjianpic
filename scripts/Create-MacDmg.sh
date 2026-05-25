#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 1 || $# -gt 2 ]]; then
  echo "Usage: $0 <path-to-app-bundle> [output-dmg]" >&2
  exit 64
fi

APP_PATH="$1"
if [[ ! -d "$APP_PATH" || "${APP_PATH##*.}" != "app" ]]; then
  echo "Expected a .app bundle directory: $APP_PATH" >&2
  exit 64
fi

APP_NAME="$(basename "$APP_PATH" .app)"
OUTPUT_DMG="${2:-${APP_NAME}.dmg}"
WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

cp -R "$APP_PATH" "$WORK_DIR/"
chmod +x "$WORK_DIR/$APP_NAME.app/Contents/MacOS/"*

if command -v codesign >/dev/null 2>&1; then
  codesign --force --deep --sign - "$WORK_DIR/$APP_NAME.app" >/dev/null
fi

ln -s /Applications "$WORK_DIR/Applications"
rm -f "$OUTPUT_DMG"
hdiutil create \
  -volname "$APP_NAME" \
  -srcfolder "$WORK_DIR" \
  -ov \
  -format UDZO \
  "$OUTPUT_DMG"
