#!/usr/bin/env bash
# Runs `az deployment sub what-if` for an environment and writes the ANSI-stripped result to
# a file (for the job summary and the PR comment). Exit code is the what-if's own.
#
# Usage: what-if.sh <fg-env> <deployment-name> <location> <create-model-deployments> <output-file>
# Requires FG_API_IMAGE (and for prod FG_SQL_ADMIN_GROUP_OBJECT_ID / _NAME) in the environment —
# the .bicepparam files read them (docs reference/infrastructure).
set -euo pipefail

FG_ENV="${1:?fg-env}"
DEPLOYMENT_NAME="${2:?deployment-name}"
LOCATION="${3:?location}"
CREATE_MODEL_DEPLOYMENTS="${4:?create-model-deployments}"
OUTPUT_FILE="${5:?output-file}"

set +e
az deployment sub what-if \
  --name "$DEPLOYMENT_NAME" \
  --location "$LOCATION" \
  --template-file infra/main.bicep \
  --parameters "infra/parameters/${FG_ENV}.bicepparam" \
  --parameters "createModelDeployments=${CREATE_MODEL_DEPLOYMENTS}" \
  --result-format FullResourcePayloads \
  2>&1 | sed -r 's/\x1B\[[0-9;]*[A-Za-z]//g' | tee "$OUTPUT_FILE"
status=${PIPESTATUS[0]}
set -e

if [ "$status" -ne 0 ]; then
  echo "::error::what-if failed with exit code ${status} (see output above)" >&2
  exit "$status"
fi

# Summary line, e.g. "Resource changes: 3 to create, 1 to modify, 56 no change."
summary_line="$(grep -E '^Resource changes:' "$OUTPUT_FILE" | tail -n1 || true)"
echo "::notice title=what-if ${FG_ENV}::${summary_line:-no summary line found}"
echo "summary=${summary_line}" >> "${GITHUB_OUTPUT:-/dev/null}"
