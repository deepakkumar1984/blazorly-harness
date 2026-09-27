# Release Rules
<!-- last-analyzed: 2026-09-27T00:00:00Z -->

## Version Sources
- Git tag only (`vX.Y.Z`). CI strips the `v` and passes `/p:Version=X.Y.Z` to the build.
- `Directory.Build.props` carries a `0.1.0` fallback used for local builds only; never bumped per release.
- No automation script; no CHANGELOG file.

## Release Trigger
- Tag push matching `v*` (`.github/workflows/release.yml`), plus `workflow_dispatch` for manual artifact-only runs.

## Test Gate
- None in CI: the release job builds (`installer/make-dist.sh`) and publishes. No test workflow exists in `.github/workflows/`.
- Local gate before tagging: `dotnet build Blazorly.Harness.slnx` must succeed.

## Registry / Distribution
- GitHub Release (created by CI via `gh release create --generate-notes`) with six platform archives + SHA256SUMS + install scripts.
- Installers (`installer/install.sh`, `install.ps1`) pull the latest release; CI marks each tag release `--latest`.

## Release Notes Strategy
- Reviewed notes in `release-notes/<tag>.md` (e.g. `release-notes/v0.9.1.md`) win: CI uses `--notes-file` when present, else `--generate-notes` from commits since the previous tag.
- No changelog convention file; commit subjects are the fallback notes source, so keep them user-facing.
- App version needs no manual bump: tag `vX.Y.Z` → CI stamps assemblies (`/p:Version=X.Y.Z`) and writes the `VERSION` marker into every archive; `AppVersion` reads the marker first, so sidebar/hero/Updates UI follow the tag automatically. `blazorly update` swaps the whole extracted archive, VERSION included.

## CI Workflow Files
- `.github/workflows/release.yml` (only workflow).

## First-Time Setup Gaps
- No CI test gate; no CHANGELOG.md. Both optional — release works without them.
