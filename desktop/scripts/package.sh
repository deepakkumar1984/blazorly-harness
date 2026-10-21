#!/usr/bin/env bash
# Package the desktop app with electron-builder.
#
#   bash scripts/package.sh mac                 # use blazorly from PATH
#   bash scripts/package.sh mac --with-engine   # build + bundle the engine
#
set -euo pipefail

cd "$(dirname "$0")/.."

target="${1:-mac}"
with_engine="${2:-}"

if [[ "$with_engine" == "--with-engine" ]]; then
  rid=""
  case "$target" in
    mac) rid="osx-arm64" ;;
    win) rid="win-x64" ;;
    linux) rid="linux-x64" ;;
  esac
  version="${BLAZORLY_VERSION:-0.0.0-dev}"
  engine="resources/engine"

  # Mirrors installer/make-dist.sh: launcher + run_code sidecar + the Web
  # project's wwwroot (which carries the SDK-injected _framework assets the
  # interactive UI needs). The whole directory is bundled into the app.
  echo "Building the engine ($rid $version)…"
  rm -rf "$engine"
  mkdir -p "$engine"
  dotnet restore ../src/Blazorly.Harness.Cli -r "$rid"
  dotnet restore ../src/Blazorly.Harness.ScriptRunner -r "$rid"
  dotnet publish ../src/Blazorly.Harness.Cli -c Release -r "$rid" --self-contained true \
    /p:BlazorlyPackaging=true /p:ErrorOnDuplicatePublishOutputFiles=false "/p:Version=$version" -o "$engine"
  dotnet publish ../src/Blazorly.Harness.ScriptRunner -c Release -r "$rid" --self-contained true \
    "/p:Version=$version" -o "$engine"
  dotnet publish ../src/Blazorly.Harness.Web -c Release -r "$rid" --self-contained true \
    /p:ErrorOnDuplicatePublishOutputFiles=false "/p:Version=$version" -o "$engine/.webassets" > /dev/null
  rm -rf "$engine/wwwroot"
  mv "$engine/.webassets/wwwroot" "$engine/wwwroot"
  rm -rf "$engine/.webassets"
  echo "$version" > "$engine/VERSION"
  echo "Engine staged in resources/engine/."
fi

npm run icons

echo "Running electron-builder (--$target)…"
CSC_IDENTITY_AUTO_DISCOVERY=false npx electron-builder "--$target" --publish never

echo "Artifacts in desktop/dist/."
