#!/usr/bin/env bash
# Builds the mod and produces a Thunderstore-ready zip in dist/.

set -euo pipefail
cd "$(dirname "$0")"

NAMESPACE="Aestelwen"
NAME="DvergrWorkers"
VERSION=$(python3 -c "import json; print(json.load(open('manifest.json'))['version_number'])")

dotnet build -c Release

rm -rf dist

OUTPUT_DIR="dist/${NAMESPACE}-${NAME}-${VERSION}"

mkdir -p "${OUTPUT_DIR}"
cp "bin/Release/${NAME}.dll" manifest.json CHANGELOG.md LICENSE README.md icon.png "${OUTPUT_DIR}/"

(cd "${OUTPUT_DIR}" && zip -r "../$NAMESPACE-$NAME-$VERSION.zip" .)

if [[ -n "${GITHUB_ENV:-}" ]]; then
    echo "NAME=${NAME}" >> "${GITHUB_ENV}"
    echo "NAMESPACE=${NAMESPACE}" >> "${GITHUB_ENV}"
    echo "VERSION=${VERSION}" >> "${GITHUB_ENV}"
fi
