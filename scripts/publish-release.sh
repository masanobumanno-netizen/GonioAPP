#!/bin/sh
set -eu
cd "$(dirname "$0")/.."
version=$(cat dist/GonioWeb-Windows-x64/version.txt)
repo=masanobumanno-netizen/GonioAPP
# Explicitly publish only binaries/manifest; source, patient data, SDK and private keys are excluded.
gh release create "v$version" --repo "$repo" --title "Gonio Web $version" --notes-file docs/RELEASE-0.4.0.md --latest \
  "dist/GonioWeb-Windows-x64-v$version.zip" \
  "dist/GonioWeb-Windows-x64-v$version.zip.sha256" \
  "dist/GonioWeb-update-$version.zip" \
  "dist/GonioWeb-update-$version.zip.sha256" \
  dist/update-manifest.json
