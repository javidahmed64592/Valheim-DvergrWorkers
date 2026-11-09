#!/usr/bin/env bash
# Fails if the mod's version differs between manifest.json, the .csproj <Version>, the plugin's
# PluginVersion constant and the top CHANGELOG.md heading. A top heading of "## Unreleased" is
# allowed (for a version spread over several pull requests); then only the first three are compared.

set -euo pipefail
cd "$(dirname "$0")/../.."

fail=0

manifest=$(python3 -c "import json; print(json.load(open('manifest.json'))['version_number'])")

csproj_file=$(find . -maxdepth 1 -name '*.csproj' | head -n 1)
csproj=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "${csproj_file}" | head -n 1)

plugin=$(grep -rhoE --include='*.cs' --exclude-dir=obj --exclude-dir=bin --exclude-dir=tests \
    'PluginVersion = "[^"]+"' . | head -n 1 | sed -E 's/.*"([^"]+)"/\1/')

changelog=$(grep -m 1 -E '^## ' CHANGELOG.md | sed -E 's/^## +//; s/ +$//')

echo "manifest.json:  ${manifest}"
echo "${csproj_file#./}: ${csproj}"
echo "PluginVersion:  ${plugin}"
echo "CHANGELOG.md:   ${changelog}"

for value in "${csproj}" "${plugin}"; do
    if [[ "${value}" != "${manifest}" ]]; then
        fail=1
    fi
done

if [[ "${changelog}" != "Unreleased" && "${changelog}" != "${manifest}" ]]; then
    fail=1
fi

if [[ "${fail}" -ne 0 ]]; then
    echo "Version mismatch: update every place above to the same version (CHANGELOG may say Unreleased)." >&2
    exit 1
fi

echo "Versions match."
