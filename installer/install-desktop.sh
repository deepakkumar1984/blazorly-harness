#!/usr/bin/env bash
# Install the Blazorly Harness desktop app (macOS, Apple Silicon) from GitHub Releases.
#
#   curl -fsSL https://raw.githubusercontent.com/deepakkumar1984/blazorly-harness/main/installer/install-desktop.sh | bash
#
# Pin a specific release:
#   BLAZORLY_VERSION=0.14.0 bash install-desktop.sh
#
# The app is ad-hoc signed today. Add the Developer ID secrets to the release
# workflow (see desktop/README.md) and future releases install without prompts.
set -euo pipefail

REPO="${BLAZORLY_REPO:-deepakkumar1984/blazorly-harness}"
VERSION="${BLAZORLY_VERSION:-}"

if [[ "$(uname -s)" != "Darwin" ]]; then
  printf 'The desktop app is macOS-only today.\n' >&2
  exit 1
fi
if [[ "$(uname -m)" != "arm64" ]]; then
  printf 'Desktop builds are Apple Silicon (arm64) only today; this Mac reports %s.\n' "$(uname -m)" >&2
  exit 1
fi

release_path="latest"
if [[ -n "$VERSION" ]]; then
  release_path="tags/v${VERSION#v}"
fi
json="$(curl -fsSL "https://api.github.com/repos/$REPO/releases/$release_path")"

asset_url() {
  printf '%s' "$json" \
    | grep -o '"browser_download_url": *"[^"]*"' \
    | cut -d'"' -f4 \
    | grep -E "$1" \
    | head -n1
}

dmg_url="$(asset_url 'Harness-[^"]*-arm64\.dmg$')"
sums_url="$(asset_url 'SHA256SUMS-desktop$')"
if [[ -z "$dmg_url" ]]; then
  printf 'No macOS dmg found in that release — build it from desktop/ (see desktop/README.md).\n' >&2
  exit 1
fi

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

printf 'Downloading %s\n' "$dmg_url"
curl -fsSL "$dmg_url" -o "$tmp/app.dmg"

if [[ -n "$sums_url" ]]; then
  printf 'Verifying checksum…\n'
  curl -fsSL "$sums_url" -o "$tmp/SHA256SUMS-desktop"
  expected="$(grep -E 'Harness-.*-arm64\.dmg$' "$tmp/SHA256SUMS-desktop" | awk '{print $1}' | head -n1)"
  actual="$(shasum -a 256 "$tmp/app.dmg" | awk '{print $1}')"
  if [[ -n "$expected" && "$expected" != "$actual" ]]; then
    printf 'Checksum mismatch — refusing to install.\n' >&2
    exit 1
  fi
fi

mount="$tmp/mnt"
mkdir -p "$mount"
hdiutil attach "$tmp/app.dmg" -nobrowse -quiet -mountpoint "$mount"
app_src="$(/bin/ls -d "$mount"/*.app | head -n1)"
app_name="$(basename "$app_src")"
dest="/Applications/$app_name"

if [[ -e "$dest" ]]; then
  printf 'Replacing existing %s\n' "$dest"
  rm -rf "$dest"
fi
cp -R "$app_src" "$dest"
hdiutil detach "$mount" -quiet

printf 'Installed %s to /Applications.\n' "$app_name"
printf 'If macOS blocks the first launch (ad-hoc signed build), right-click the app and choose Open once.\n'
