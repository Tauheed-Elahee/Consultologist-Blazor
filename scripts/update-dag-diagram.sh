#!/usr/bin/env bash
# Regenerate packages/general/dag.mmd from manifest.json.
#
# dag.mmd is a derived view: WorkflowDagDiagram.Generate computes it from the
# manifest's nodes and bindings — it is never edited by hand. Run this after any
# manifest change and commit both files together. Generation is local and
# pre-commit only: `git push` never generates anything — CI regenerates in memory
# and FAILS the build if the committed file is stale (the snapshot-pin test
# WorkflowDagDiagramTests.GeneratedDiagram_MatchesCheckedInFile).
#
# Usage:
#   ./scripts/update-dag-diagram.sh
set -euo pipefail

cd "$(dirname "$0")/.."

UPDATE_SNAPSHOTS=1 dotnet test tests/Consultologist.Api.Tests.csproj \
	--filter "FullyQualifiedName~WorkflowDagDiagramTests" --nologo

echo "Regenerated packages/general/dag.mmd — review and commit it with the manifest change."
