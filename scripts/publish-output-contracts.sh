#!/usr/bin/env bash
# Publish the output-contract catalog to the PUBLIC registry and update its
# latest-pointer. The catalog is a versioned artifact (agents/output-contracts.json
# declares its own CalVer version); published versions are immutable — this script
# refuses to overwrite an existing version. The engine loads the pinned version at
# startup (OutputContracts__Pin, default output-contracts@latest), so activating a
# new catalog is: publish, bump the pin if concrete, restart the Function App.
#
# Usage:
#   ./scripts/publish-output-contracts.sh <storage-account>
# Example:
#   ./scripts/publish-output-contracts.sh consultologistpublic
set -euo pipefail

CONTAINER="output-contracts"
AGENTS_DIR="agents"
CATALOG="$AGENTS_DIR/output-contracts.json"

if [[ $# -ne 1 ]]; then
	grep '^#' "$0" | sed 's/^# \{0,1\}//' | head -11
	exit 1
fi

ACCOUNT="$1"

[[ -f "$CATALOG" ]] || { echo "error: $CATALOG not found (run from the repo root)" >&2; exit 1; }

VERSION=$(python3 -c "import json;print(json.load(open('$CATALOG'))['version'])")

if ! [[ "$VERSION" =~ ^v[0-9]{4}\.[0-9]{2}\.[1-9][0-9]*$ ]]; then
	echo "error: catalog version '$VERSION' is not vYYYY.MM.N" >&2
	exit 1
fi

AUTH=(--account-name "$ACCOUNT" --auth-mode "${AZ_STORAGE_AUTH_MODE:-login}")

if az storage blob exists "${AUTH[@]}" --container-name "$CONTAINER" \
	--name "$VERSION/output-contracts.json" --query exists -o tsv | grep -q true; then
	echo "error: output-contracts@$VERSION is already published; versions are immutable — bump the version" >&2
	exit 1
fi

# Schema files first, catalog json last (the loader resolves the catalog first,
# so a partial upload is invisible).
python3 -c "
import json
for e in json.load(open('$CATALOG'))['contracts'].values():
    f = e.get('schemaFile')
    if f: print(f)
" | sort -u | while read -r SCHEMA; do
	[[ -f "$AGENTS_DIR/$SCHEMA" ]] || { echo "error: schema file $AGENTS_DIR/$SCHEMA not found" >&2; exit 1; }
	echo "Uploading $VERSION/$SCHEMA"
	az storage blob upload "${AUTH[@]}" --container-name "$CONTAINER" \
		--file "$AGENTS_DIR/$SCHEMA" --name "$VERSION/$SCHEMA" --output none
done

echo "Uploading $VERSION/output-contracts.json"
az storage blob upload "${AUTH[@]}" --container-name "$CONTAINER" \
	--file "$CATALOG" --name "$VERSION/output-contracts.json" --output none

echo "Updating latest.json -> $VERSION"
POINTER=$(mktemp)
printf '{"version": "%s"}\n' "$VERSION" > "$POINTER"
az storage blob upload "${AUTH[@]}" --container-name "$CONTAINER" \
	--file "$POINTER" --name "latest.json" --overwrite --output none
rm -f "$POINTER"

echo "Published output-contracts@$VERSION and updated latest pointer."
