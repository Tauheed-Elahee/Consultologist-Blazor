#!/usr/bin/env bash
# Publish a workflow package to the registry (Azure Blob Storage) and update its
# latest-pointer. Published versions are immutable: this script refuses to overwrite
# an existing version.
#
# Usage:
#   ./scripts/publish-workflow-package.sh <storage-account> <package-dir>
#
# <package-dir> must contain manifest.json (with name, version "vYYYY.MM.N", specVersion)
# and standards.md. Example:
#   ./scripts/publish-workflow-package.sh mystorageaccount packages/general
set -euo pipefail

CONTAINER="workflow-packages"

if [[ $# -ne 2 ]]; then
	grep '^#' "$0" | sed 's/^# \{0,1\}//' | head -9
	exit 1
fi

ACCOUNT="$1"
PACKAGE_DIR="$2"

MANIFEST="$PACKAGE_DIR/manifest.json"
STANDARDS="$PACKAGE_DIR/standards.md"
[[ -f "$MANIFEST" && -f "$STANDARDS" ]] || { echo "error: $PACKAGE_DIR must contain manifest.json and standards.md" >&2; exit 1; }

NAME=$(python3 -c "import json;print(json.load(open('$MANIFEST'))['name'])")
VERSION=$(python3 -c "import json;print(json.load(open('$MANIFEST'))['version'])")

if ! [[ "$VERSION" =~ ^v[0-9]{4}\.[0-9]{2}\.[1-9][0-9]*$ ]]; then
	echo "error: version '$VERSION' is not vYYYY.MM.N (zero-padded month, counter >= 1)" >&2
	exit 1
fi

# login (Entra RBAC, needs Storage Blob Data Contributor) or key (queries account keys)
AUTH=(--account-name "$ACCOUNT" --auth-mode "${AZ_STORAGE_AUTH_MODE:-login}")

az storage container create "${AUTH[@]}" --name "$CONTAINER" --output none

if az storage blob exists "${AUTH[@]}" --container-name "$CONTAINER" \
	--name "$NAME/$VERSION/manifest.json" --query exists -o tsv | grep -q true; then
	echo "error: $NAME@$VERSION is already published; versions are immutable — bump the version" >&2
	exit 1
fi

SPEC_VERSION=$(python3 -c "import json;print(json.load(open('$MANIFEST'))['specVersion'])")
if [[ "$SPEC_VERSION" -ge 2 && ! -d "$PACKAGE_DIR/prompts" ]]; then
	echo "error: specVersion $SPEC_VERSION packages must contain a prompts/ directory" >&2
	exit 1
fi

echo "Publishing $NAME@$VERSION ..."
az storage blob upload "${AUTH[@]}" --container-name "$CONTAINER" \
	--file "$MANIFEST" --name "$NAME/$VERSION/manifest.json" --output none
az storage blob upload "${AUTH[@]}" --container-name "$CONTAINER" \
	--file "$STANDARDS" --name "$NAME/$VERSION/standards.md" --output none

if [[ -d "$PACKAGE_DIR/prompts" ]]; then
	for f in "$PACKAGE_DIR"/prompts/*.md; do
		az storage blob upload "${AUTH[@]}" --container-name "$CONTAINER" \
			--file "$f" --name "$NAME/$VERSION/prompts/$(basename "$f")" --output none
	done
fi

echo "Updating $NAME/latest.json -> $VERSION"
POINTER=$(mktemp)
printf '{"version": "%s"}\n' "$VERSION" > "$POINTER"
az storage blob upload "${AUTH[@]}" --container-name "$CONTAINER" \
	--file "$POINTER" --name "$NAME/latest.json" --overwrite --output none
rm -f "$POINTER"

echo "Published $NAME@$VERSION and updated latest pointer."
