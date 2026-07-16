#!/usr/bin/env bash
# Publish a repo-owned workflow package to the PUBLIC registry (Azure Blob Storage)
# and update its latest-pointer. Published versions are immutable: this script
# refuses to overwrite an existing version. Since the ownership split (#92),
# repo-owned packages live in the public account (consultologistpublic) —
# acct-* forks are published only by the app's registry writer, never this script.
#
# Usage:
#   ./scripts/publish-workflow-package.sh <storage-account> <package-dir>
#
# <package-dir> must contain manifest.json (with name, version "vYYYY.MM.N", specVersion).
# Example:
#   ./scripts/publish-workflow-package.sh consultologistpublic packages/general
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
[[ -f "$MANIFEST" ]] || { echo "error: $PACKAGE_DIR must contain manifest.json" >&2; exit 1; }

NAME=$(python3 -c "import json;print(json.load(open('$MANIFEST'))['name'])")
VERSION=$(python3 -c "import json;print(json.load(open('$MANIFEST'))['version'])")

if [[ "$NAME" == acct-* ]]; then
	echo "error: '$NAME' is an account package; acct-* forks are published only by the app's registry writer (private registry)" >&2
	exit 1
fi

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

HAS_SCHEMAS=$(python3 -c "import json;print(1 if json.load(open('$MANIFEST')).get('schemas') else 0)")
if [[ "$HAS_SCHEMAS" == "1" && ! -d "$PACKAGE_DIR/schemas" ]]; then
	echo "error: the manifest declares schemas but $PACKAGE_DIR/schemas is missing" >&2
	exit 1
fi

HAS_DATA=$(python3 -c "import json;print(1 if json.load(open('$MANIFEST')).get('data') else 0)")
if [[ "$HAS_DATA" == "1" && ! -d "$PACKAGE_DIR/data" ]]; then
	echo "error: the manifest declares data but $PACKAGE_DIR/data is missing" >&2
	exit 1
fi

echo "Publishing $NAME@$VERSION ..."
az storage blob upload "${AUTH[@]}" --container-name "$CONTAINER" \
	--file "$MANIFEST" --name "$NAME/$VERSION/manifest.json" --output none
if [[ -f "$STANDARDS" ]]; then
	az storage blob upload "${AUTH[@]}" --container-name "$CONTAINER" \
		--file "$STANDARDS" --name "$NAME/$VERSION/standards.md" --output none
fi

if [[ -d "$PACKAGE_DIR/prompts" ]]; then
	for f in "$PACKAGE_DIR"/prompts/*.md; do
		az storage blob upload "${AUTH[@]}" --container-name "$CONTAINER" \
			--file "$f" --name "$NAME/$VERSION/prompts/$(basename "$f")" --output none
	done
fi

if [[ -d "$PACKAGE_DIR/schemas" ]]; then
	for f in "$PACKAGE_DIR"/schemas/*.json; do
		az storage blob upload "${AUTH[@]}" --container-name "$CONTAINER" \
			--file "$f" --name "$NAME/$VERSION/schemas/$(basename "$f")" --output none
	done
fi

# Data collections and scalars upload recursively, preserving collection-relative
# paths (index.json + item files; package-format-v5.md).
if [[ -d "$PACKAGE_DIR/data" ]]; then
	while IFS= read -r -d '' f; do
		rel="${f#"$PACKAGE_DIR"/}"
		az storage blob upload "${AUTH[@]}" --container-name "$CONTAINER" \
			--file "$f" --name "$NAME/$VERSION/$rel" --output none
	done < <(find "$PACKAGE_DIR/data" -type f -print0)
fi

# The derived DAG diagram rides along when present (generated, never authored;
# pinned to the generator by WorkflowDagDiagramTests).
if [[ -f "$PACKAGE_DIR/dag.mmd" ]]; then
	az storage blob upload "${AUTH[@]}" --container-name "$CONTAINER" \
		--file "$PACKAGE_DIR/dag.mmd" --name "$NAME/$VERSION/dag.mmd" --output none
fi

echo "Updating $NAME/latest.json -> $VERSION"
POINTER=$(mktemp)
printf '{"version": "%s"}\n' "$VERSION" > "$POINTER"
az storage blob upload "${AUTH[@]}" --container-name "$CONTAINER" \
	--file "$POINTER" --name "$NAME/latest.json" --overwrite --output none
rm -f "$POINTER"

echo "Published $NAME@$VERSION and updated latest pointer."
