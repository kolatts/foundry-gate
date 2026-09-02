#!/usr/bin/env bash
# The one `az deployment sub create` in the repo. Used by _deploy-infra.yml (the infra stage) and
# by _deploy-api.yml (the day-0 bootstrap replacement, which re-runs the same template with a new
# FG_API_IMAGE so Bicep flips the ingress port and the probes together with the image).
#
# Usage: deploy.sh <fg-env> <deployment-name> <location> <create-model-deployments>
#   fg-env                    dev | prod         (the Bicep/FoundryGate name, not the GitHub one)
#   create-model-deployments  true | false       Anthropic deployments are create-once under ARM
#                                                (CLAUDE.md / fable-refactor-log E-007) — only a
#                                                brand-new environment's first run passes true.
#
# The .bicepparam files read FG_API_IMAGE (and for prod FG_SQL_ADMIN_GROUP_OBJECT_ID / _NAME)
# from the environment with no default, on purpose: a forgotten image variable must never
# silently swap the running API for the placeholder page. Resolve it with resolve-api-image.sh.
set -euo pipefail

FG_ENV="${1:?usage: deploy.sh <dev|prod> <deployment-name> <location> <create-model-deployments>}"
DEPLOYMENT_NAME="${2:?deployment-name}"
LOCATION="${3:?location}"
CREATE_MODEL_DEPLOYMENTS="${4:?create-model-deployments}"

if [ -z "${FG_API_IMAGE:-}" ]; then
  echo "::error::FG_API_IMAGE is empty. infra/parameters/${FG_ENV}.bicepparam requires it and deploying without it would reset the Container App. Run .github/scripts/infra/resolve-api-image.sh first." >&2
  exit 1
fi

echo "Deploying ${DEPLOYMENT_NAME} (${FG_ENV}) in ${LOCATION} with FG_API_IMAGE=${FG_API_IMAGE}, createModelDeployments=${CREATE_MODEL_DEPLOYMENTS}"

az deployment sub create \
  --name "$DEPLOYMENT_NAME" \
  --location "$LOCATION" \
  --template-file infra/main.bicep \
  --parameters "infra/parameters/${FG_ENV}.bicepparam" \
  --parameters "createModelDeployments=${CREATE_MODEL_DEPLOYMENTS}" \
  --only-show-errors \
  --output none
