#!/usr/bin/env bash
# The one `az deployment sub create` in the repo. Used by _deploy-infra.yml (the infra stage) and
# by _deploy-api.yml (the day-0 bootstrap replacement, which re-runs the same template with a new
# FG_API_IMAGE so Bicep flips the ingress port and the probes together with the image).
#
# Usage: deploy.sh <fg-env> <deployment-name> <location> <create-model-deployments>
#   fg-env                    dev | prod         (the Bicep/FoundryGate name, not the GitHub one)
#   create-model-deployments  true | false       Maps to createAnthropicModelDeployments. Anthropic
#                                                deployments are create-once under ARM (CLAUDE.md /
#                                                fable-refactor-log E-007) — only a brand-new
#                                                environment's first run passes true. OpenAI
#                                                deployments ignore it and are reconciled on
#                                                every run (#259).
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

echo "Deploying ${DEPLOYMENT_NAME} (${FG_ENV}) in ${LOCATION} with FG_API_IMAGE=${FG_API_IMAGE}, createAnthropicModelDeployments=${CREATE_MODEL_DEPLOYMENTS}"

az deployment sub create \
  --name "$DEPLOYMENT_NAME" \
  --location "$LOCATION" \
  --template-file infra/main.bicep \
  --parameters "infra/parameters/${FG_ENV}.bicepparam" \
  --parameters "createAnthropicModelDeployments=${CREATE_MODEL_DEPLOYMENTS}" \
  --only-show-errors \
  --output none

# The capacity ceiling (#260): a tier whose TPM exceeds what its aliases' deployments can serve
# throttles on the deployment before the developer's own meter is ever reached. Bicep computes the
# shortfalls; this surfaces them where someone will see them. Never fatal - capacity is a human
# decision, and for Claude a create-once one (#205).
WARNINGS=$(az deployment sub show --name "$DEPLOYMENT_NAME" \
  --query "properties.outputs.modelCapacityWarnings.value" --output json 2>/dev/null || echo '[]')

echo "$WARNINGS" | python3 -c '
import json, sys

try:
    rows = json.load(sys.stdin) or []
except (ValueError, TypeError):
    sys.exit(0)

for r in rows:
    print(
        "::warning title=Model capacity below tier TPM::"
        "Tier {tier} allows {tierTpm} TPM, but alias {alias} routes at deployment {deployment}, "
        "which can serve {deploymentTpm} TPM. Developers on this tier are throttled by the "
        "deployment before their own meter is ever reached (#260).".format(**r))
'
