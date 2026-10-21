#!/bin/sh
# blazorly installer — Linux/macOS (pi.dev style one-liner):
#   curl -fsSL https://raw.githubusercontent.com/deepakkumar1984/blazorly-harness/main/installer/install.sh | sh
# Offline / local testing: BLAZORLY_INSTALL_BASE=/path/to/dist sh installer/install.sh
set -eu

REPO="${BLAZORLY_REPO:-deepakkumar1984/blazorly-harness}"
BASE="${BLAZORLY_INSTALL_BASE:-https://github.com/$REPO/releases/latest/download}"
# local dist testing: a plain directory becomes a file:// URL
case "$BASE" in
  /*|.) BASE="file://$(cd "$BASE" && pwd)" ;;
  ./*) BASE="file://$(cd "${BASE#./}" && pwd)" ;;
esac

say() { printf '\033[1;34m==>\033[0m %s\n' "$1"; }
die() { printf '\033[1;31merror:\033[0m %s\n' "$1" >&2; exit 1; }

os=$(uname -s)
case "$os" in
  Linux) rid_os=linux ;;
  Darwin) rid_os=osx ;;
  *) die "unsupported OS '$os' — on Windows use install.ps1: irm https://raw.githubusercontent.com/$REPO/main/installer/install.ps1 | iex" ;;
esac

arch=$(uname -m)
case "$arch" in
  x86_64|amd64) rid_arch=x64 ;;
  aarch64|arm64) rid_arch=arm64 ;;
  *) die "unsupported architecture '$arch'" ;;
esac
rid="$rid_os-$rid_arch"
archive="blazorly-$rid.tar.gz"

say "detecting latest release"
asset="$BASE/$archive"
checksum="$BASE/$archive.sha256"

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

say "downloading $archive"
curl -fsSL "$asset" -o "$tmp/$archive" || die "download failed — is a release published for $rid?"

if [ "${BLAZORLY_SKIP_VERIFY:-0}" != "1" ]; then
  say "verifying checksum"
  if curl -fsSL "$checksum" -o "$tmp/$archive.sha256" 2>/dev/null; then
    want=$(awk '{print $1}' "$tmp/$archive.sha256")
    got=$(sha256sum "$tmp/$archive" | awk '{print $1}')
    [ "$want" = "$got" ] || die "checksum mismatch — aborting (set BLAZORLY_SKIP_VERIFY=1 to skip)"
  else
    echo "    no checksum published; skipping verification"
  fi
fi

dest="${BLAZORLY_INSTALL_DIR:-$HOME/.blazorly/app}"
mkdir -p "$dest"
rm -rf "$dest/.incoming"
mkdir "$dest/.incoming"
tar -xzf "$tmp/$archive" -C "$dest/.incoming"
rm -rf "$dest/current"
mv "$dest/.incoming" "$dest/current"
version=$(cat "$dest/current/VERSION" 2>/dev/null || echo "?")
say "installed blazorly $version to $dest/current"

bin_dir="${BLAZORLY_BIN_DIR:-$HOME/.local/bin}"
mkdir -p "$bin_dir"
if [ -n "${BLAZORLY_NO_SYMLINK:-}" ]; then
  ln -sf "$dest/current/blazorly" "$bin_dir/blazorly" 2>/dev/null || true
else
  ln -sf "$dest/current/blazorly" "$bin_dir/blazorly"
fi

case ":$PATH:" in
  *":$bin_dir:"*) ;;
  *) printf '\033[1;33mnote:\033[0m %s is not on your PATH — add it:\n  echo '\''export PATH="%s:$PATH"'\'' >> ~/.profile && source ~/.profile\n' "$bin_dir" "$bin_dir" ;;
esac

say "run \`blazorly\` to start the UI (http://localhost:5080), \`blazorly --help\` for all modes"
if [ "$rid_os" = "osx" ]; then
  printf '\033[1;33mnote:\033[0m macOS has no Landlock sandbox: bash/run_code fail closed until you\nswitch the session permission preset to danger-full-access (/permission).\n'
fi
