#!/usr/bin/env bash
# Builds the mod, deploys all mod files to your r2modman profile, and produces a Thunderstore-ready zip.

set -euo pipefail
cd "$(dirname "$0")"

NAMESPACE="Aestelwen"
NAME="DvergrWorkers"
VERSION=$(python3 -c "import json; print(json.load(open('manifest.json'))['version_number'])")

dotnet build -c Release

rm -rf dist

OUTPUT_DIR="dist/$NAMESPACE-$NAME-$VERSION"

mkdir -p "$OUTPUT_DIR"
cp "bin/Release/$NAME.dll" manifest.json CHANGELOG.md LICENSE README.md icon.png "$OUTPUT_DIR/"

(cd "$OUTPUT_DIR" && zip -r "../$NAMESPACE-$NAME-$VERSION.zip" .)

# Export version for the GitHub Actions artifact upload step
if [[ -n "${GITHUB_ENV:-}" ]]; then
    echo "VERSION=$VERSION" >> "$GITHUB_ENV"
fi
