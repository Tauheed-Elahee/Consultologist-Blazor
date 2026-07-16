#!/usr/bin/env bash
# Publish a REDACTED agent definition to the public registry, keyed by the
# Foundry version sequence (the version the YAML itself declares — the same
# number job records store in agentVersions). Published versions are immutable:
# this script refuses to overwrite. The redaction strips the tool plumbing
# (tools[].server_url, project_connection_id) and must stay line-for-line
# equivalent to AgentDefinitionRedaction.Redact — startup attestation compares
# the published artifact to Redact(bundled manifest) and fails loud on drift.
#
# Usage:
#   ./scripts/publish-agent-definition.sh <storage-account> <agent-name>
# Example:
#   ./scripts/publish-agent-definition.sh consultologistpublic concept-extraction
set -euo pipefail

CONTAINER="agent-definitions"

if [[ $# -ne 2 ]]; then
	grep '^#' "$0" | sed 's/^# \{0,1\}//' | head -12
	exit 1
fi

ACCOUNT="$1"
NAME="$2"
MANIFEST="agents/$NAME.yaml"

[[ -f "$MANIFEST" ]] || { echo "error: $MANIFEST not found (run from the repo root)" >&2; exit 1; }

VERSION=$(grep -m1 '^version:' "$MANIFEST" | sed 's/^version:[[:space:]]*"\{0,1\}\([^"]*\)"\{0,1\}[[:space:]]*$/\1/')

[[ -n "$VERSION" ]] || { echo "error: $MANIFEST declares no top-level version" >&2; exit 1; }

AUTH=(--account-name "$ACCOUNT" --auth-mode "${AZ_STORAGE_AUTH_MODE:-login}")

if az storage blob exists "${AUTH[@]}" --container-name "$CONTAINER" \
	--name "$NAME/$VERSION/definition.yaml" --query exists -o tsv | grep -q true; then
	echo "error: $NAME/$VERSION is already published; versions are immutable" >&2
	exit 1
fi

# The redaction — keep line-for-line equivalent to AgentDefinitionRedaction.Redact.
REDACTED=$(mktemp)
sed '/^[[:space:]]*server_url:/d; /^[[:space:]]*project_connection_id:/d' "$MANIFEST" > "$REDACTED"

if grep -qE 'server_url|project_connection_id' "$REDACTED"; then
	echo "error: redaction left plumbing fields behind — refusing to publish" >&2
	rm -f "$REDACTED"
	exit 1
fi

echo "Uploading $NAME/$VERSION/definition.yaml (redacted)"
az storage blob upload "${AUTH[@]}" --container-name "$CONTAINER" \
	--file "$REDACTED" --name "$NAME/$VERSION/definition.yaml" --output none
rm -f "$REDACTED"

echo "Updating $NAME/latest.json -> $VERSION"
POINTER=$(mktemp)
printf '{"version": "%s"}\n' "$VERSION" > "$POINTER"
az storage blob upload "${AUTH[@]}" --container-name "$CONTAINER" \
	--file "$POINTER" --name "$NAME/latest.json" --overwrite --output none
rm -f "$POINTER"

echo "Published $NAME/$VERSION (redacted) and updated latest pointer."
