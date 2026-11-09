#!/usr/bin/env bash
# Builds the mod and produces a Thunderstore-ready zip in dist/.
# The mod's name and version come from manifest.json and the DLL name from the .csproj, so this script is the
# same in every mod repo.

set -euo pipefail
cd "$(dirname "$0")/../.."

NAMESPACE="Aestelwen"
NAME=$(python3 -c "import json; print(json.load(open('manifest.json'))['name'])")
VERSION=$(python3 -c "import json; print(json.load(open('manifest.json'))['version_number'])")

CSPROJ=$(find . -maxdepth 1 -name '*.csproj' | head -n 1)
ASSEMBLY=$(sed -n 's:.*<AssemblyName>\(.*\)</AssemblyName>.*:\1:p' "${CSPROJ}" | head -n 1)
ASSEMBLY="${ASSEMBLY:-${NAME}}"

dotnet build "${CSPROJ}" -c Release

rm -rf dist

OUTPUT_DIR="dist/${NAMESPACE}-${NAME}-${VERSION}"

mkdir -p "${OUTPUT_DIR}"
cp "bin/Release/${ASSEMBLY}.dll" manifest.json CHANGELOG.md LICENSE README.md icon.png "${OUTPUT_DIR}/"

(cd "${OUTPUT_DIR}" && zip -r "../${NAMESPACE}-${NAME}-${VERSION}.zip" .)

if [[ -n "${GITHUB_ENV:-}" ]]; then
    echo "NAME=${NAME}" >> "${GITHUB_ENV}"
    echo "NAMESPACE=${NAMESPACE}" >> "${GITHUB_ENV}"
    echo "VERSION=${VERSION}" >> "${GITHUB_ENV}"
fi
