#!/usr/bin/env bash
# Build self-contained blazorly distributions for every supported platform.
#   ./installer/make-dist.sh                     all 6 RIDs
#   BLAZORLY_RIDS="linux-arm64" ./installer/...  one RID (quick local smoke)
# Output: dist/blazorly-<rid>.tar.gz|.zip + dist/SHA256SUMS
set -euo pipefail

VERSION="${BLAZORLY_VERSION:-0.0.0-dev}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${BLAZORLY_DIST_DIR:-$ROOT/dist}"
RIDS="${BLAZORLY_RIDS:-linux-arm64 linux-x64 win-x64 win-arm64 osx-x64 osx-arm64}"

mkdir -p "$OUT"
for rid in $RIDS; do
  stage="$OUT/stage/$rid"
  echo "==> publishing $rid ($VERSION)"
  rm -rf "$stage"
  mkdir -p "$stage"

  # explicit restore per RID: implicit restores race with MSBuild node reuse across
  # back-to-back RID publishes and can fail with NETSDK1047 (stale assets graph)
  dotnet restore "$ROOT/src/Blazorly.Harness.Cli" -r "$rid"
  dotnet restore "$ROOT/src/Blazorly.Harness.ScriptRunner" -r "$rid"

  # the product launcher: UI by default, all CLI modes — renamed to `blazorly`.
  # ErrorOnDuplicatePublishOutputFiles=false: the ScriptRunner exe reference drags its
  # own apphost/deps artifacts into the graph; the dedicated publish below overwrites
  # them with the RID-correct files, so the stage ends deterministic.
  dotnet publish "$ROOT/src/Blazorly.Harness.Cli" -c Release -r "$rid" --self-contained true \
    /p:BlazorlyPackaging=true /p:ErrorOnDuplicatePublishOutputFiles=false "/p:Version=$VERSION" -o "$stage"

  # run_code sidecar: CodeMode looks for it beside the host
  dotnet publish "$ROOT/src/Blazorly.Harness.ScriptRunner" -c Release -r "$rid" --self-contained true \
    "/p:Version=$VERSION" -o "$stage"

  # UI assets: an Exe-to-Exe reference does not flow Web's wwwroot into the launcher's
  # publish — stage it explicitly. Static files are served plainly in this layout
  # (see UiHost), so no static-web-assets manifest is needed.
  cp -r "$ROOT/src/Blazorly.Harness.Web/wwwroot" "$stage/wwwroot"

  echo "$VERSION" > "$stage/VERSION"

  case "$rid" in
    win-*)
      archive="$OUT/blazorly-$rid.zip"
      # zip the stage CONTENTS (flat, like the tar.gz) — a nested top-level dir would
      # break Expand-Archive's layout in the installer
      (cd "$stage" && python3 -m zipfile -c "$archive" .)
      ;;
    *)
      archive="$OUT/blazorly-$rid.tar.gz"
      tar -czf "$archive" -C "$stage" .
      ;;
  esac
  rm -rf "$stage"
  echo "    $archive ($(du -h "$archive" | cut -f1))"
done

cd "$OUT"
: > SHA256SUMS
for f in blazorly-*.tar.gz blazorly-*.zip; do
  [ -e "$f" ] || continue
  sha256sum "$f" >> SHA256SUMS
  sha256sum "$f" > "$f.sha256"
done
echo "==> done: $OUT/SHA256SUMS"
