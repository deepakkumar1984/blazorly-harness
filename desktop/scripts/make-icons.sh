#!/usr/bin/env bash
# Build the app icon set from assets/brand.svg and the tray template images.
#
# Rendering uses headless Chrome (any Chrome-family browser works); the .icns
# is assembled with sips + iconutil, both part of macOS. Run from the desktop
# directory: npm run icons

set -euo pipefail

cd "$(dirname "$0")/.."

find_chrome() {
  local candidates=(
    "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"
    "/Applications/Chromium.app/Contents/MacOS/Chromium"
    "/Applications/Brave Browser.app/Contents/MacOS/Brave Browser"
    "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge"
  )
  for candidate in "${candidates[@]}"; do
    if [[ -x "$candidate" ]]; then echo "$candidate"; return 0; fi
  done
  return 1
}

CHROME="$(find_chrome)" || {
  echo "No Chrome-family browser found; install one or convert assets/brand.svg manually." >&2
  exit 1
}

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

render() { # svg, size, out
  local svg="$1" size="$2" out="$3"
  cat > "$work/wrap.html" <<HTML
<!doctype html><html><head><style>html,body{margin:0;width:100%;height:100%;background:transparent}svg{width:100%;height:100%;display:block}</style></head>
<body>$(cat "$svg")</body></html>
HTML
  "$CHROME" --headless=new --disable-gpu --hide-scrollbars \
    --default-background-color=00000000 \
    --window-size="$size,$size" --screenshot="$out" \
    "file://$work/wrap.html" >/dev/null 2>&1
}

echo "Rendering assets/icon.png (1024px)…"
render assets/brand.svg 1024 assets/icon.png

if command -v sips >/dev/null && command -v iconutil >/dev/null; then
  echo "Building assets/icon.icns…"
  iconset="$work/icon.iconset"
  mkdir -p "$iconset"
  for spec in "16 icon_16x16" "32 icon_16x16@2x" "32 icon_32x32" "64 icon_32x32@2x" \
              "128 icon_128x128" "256 icon_128x128@2x" "256 icon_256x256" \
              "512 icon_256x256@2x" "512 icon_512x512" "1024 icon_512x512@2x"; do
    set -- $spec
    sips -z "$1" "$1" assets/icon.png --out "$iconset/$2.png" >/dev/null
  done
  iconutil -c icns "$iconset" -o assets/icon.icns
else
  echo "sips/iconutil unavailable (not macOS?) — skipping .icns" >&2
fi

echo "Rendering tray template images…"
render assets/trayTemplate.svg 16 assets/trayTemplate.png
render assets/trayTemplate.svg 32 "assets/trayTemplate@2x.png"

echo "Done."
