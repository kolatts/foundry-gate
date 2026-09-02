#!/usr/bin/env bash
# Resolves the value of FG_API_IMAGE for an infra run (PR #111 re-run invariant 2, docs
# reference/infrastructure "Deploying"): the image the API Container App is CURRENTLY running,
# so a Bicep re-run never resets the app to the placeholder.
#
# The ONLY non-fatal path is a genuine day-0 run:
#   * the subscription deployment record `foundrygate-{env}` does not exist yet, or
#   * it exists but carries no containerAppName/resourceGroupName (the control plane half has
#     not been deployed).
# Everything else — auth, throttling, a wrong subscription, a resource group that was deleted
# behind the deployment record, an app that reports no image — is FATAL. Silently falling back
# to the placeholder would swap the running API for the k8se quickstart page, which is exactly
# what `infra/parameters/prod.bicepparam` says must never happen.
#
# Usage: resolve-api-image.sh <fg-env> [deployment-name]      # fg-env = dev | prod
# Writes `image=<ref>` to $GITHUB_OUTPUT (so the caller cannot swallow a non-zero exit inside a
# command substitution) and also prints the reference on stdout.
#
# Testing: set FG_AZ to a stub to exercise every branch without Azure —
#   bash .github/scripts/infra/resolve-api-image.test.sh
set -euo pipefail

FG_ENV="${1:?usage: resolve-api-image.sh <dev|prod> [deployment-name]}"
DEPLOYMENT_NAME="${2:-foundrygate-${FG_ENV}}"
PLACEHOLDER="mcr.microsoft.com/k8se/quickstart:latest"
AZ="${FG_AZ:-az}"

stderr_file="$(mktemp)"
trap 'rm -f "$stderr_file"' EXIT

emit() {
  echo "image=$1" >> "${GITHUB_OUTPUT:-/dev/null}"
  echo "$1"
}

is_not_found() {
  grep -qiE "ResourceNotFound|DeploymentNotFound|could not be found|does not exist|was not found|not found" "$1"
}

# 1. The deployment record. `az deployment sub show` is the same source every other workflow in
#    this repo reads its resource names from (export-outputs.sh).
if ! outputs_json="$("$AZ" deployment sub show --name "$DEPLOYMENT_NAME" \
  --query properties.outputs -o json 2>"$stderr_file")"; then
  if is_not_found "$stderr_file"; then
    echo "::notice::Subscription deployment ${DEPLOYMENT_NAME} does not exist yet — day-0 run, using placeholder image ${PLACEHOLDER}" >&2
    emit "$PLACEHOLDER"
    exit 0
  fi
  echo "::error::Could not read the subscription deployment ${DEPLOYMENT_NAME} (not a not-found error). Refusing to fall back to the placeholder — that would reset the running API." >&2
  cat "$stderr_file" >&2
  exit 1
fi

if [ -z "$outputs_json" ] || [ "$outputs_json" = "null" ]; then
  echo "::notice::Subscription deployment ${DEPLOYMENT_NAME} has no outputs yet — day-0 run, using placeholder image ${PLACEHOLDER}" >&2
  emit "$PLACEHOLDER"
  exit 0
fi

container_app="$(jq -r '.containerAppName.value // "" | tostring' <<<"$outputs_json")"
resource_group="$(jq -r '.resourceGroupName.value // "" | tostring' <<<"$outputs_json")"

# 2. Control plane not deployed yet (deployControlPlane=false, or the first half of day 0).
if [ -z "$container_app" ] && [ -z "$resource_group" ]; then
  echo "::notice::${DEPLOYMENT_NAME} outputs carry no containerAppName/resourceGroupName — the control plane is not deployed yet, using placeholder image ${PLACEHOLDER}" >&2
  emit "$PLACEHOLDER"
  exit 0
fi

if [ -z "$container_app" ]; then
  echo "::notice::${DEPLOYMENT_NAME} has resourceGroupName=${resource_group} but no containerAppName — the control plane is not deployed yet, using placeholder image ${PLACEHOLDER}" >&2
  emit "$PLACEHOLDER"
  exit 0
fi

if [ -z "$resource_group" ]; then
  echo "::error::${DEPLOYMENT_NAME} reports containerAppName=${container_app} but no resourceGroupName. Refusing to guess the resource group." >&2
  exit 1
fi

# 3. The deployment says the app exists, so ANY failure reading it is fatal — including
#    ResourceNotFound, which means the resource group was emptied behind the deployment record.
if ! image="$("$AZ" containerapp show --name "$container_app" --resource-group "$resource_group" \
  --query 'properties.template.containers[0].image' -o tsv 2>"$stderr_file")"; then
  echo "::error::${DEPLOYMENT_NAME} names Container App ${container_app} in ${resource_group}, but reading its current image failed. Refusing to fall back to the placeholder — that would reset the running API. Re-run infra with create-model-deployments off after fixing the cause, or delete the deployment record to force a day-0 run." >&2
  cat "$stderr_file" >&2
  exit 1
fi

if [ -z "$image" ]; then
  echo "::error::${container_app} exists but reports no container image — refusing to guess." >&2
  exit 1
fi

echo "::notice::${container_app} currently runs ${image}; passing it as FG_API_IMAGE" >&2
emit "$image"
