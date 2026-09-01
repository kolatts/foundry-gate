#!/usr/bin/env bash
# Resolves the value of FG_API_IMAGE for an infra run (PR #111 re-run invariant 2, docs
# reference/infrastructure "Deploying"): the image the API Container App is CURRENTLY running,
# so a Bicep re-run never resets the app to the placeholder. Falls back to the public bootstrap
# placeholder only when the resource group or the Container App genuinely does not exist yet
# (day 0). Any OTHER failure (auth, throttling, extension) is fatal — silently falling back
# would swap the running API for a placeholder page.
#
# Usage: resolve-api-image.sh <fg-env>          # fg-env = dev | prod
# Prints the image reference on stdout.
set -euo pipefail

FG_ENV="${1:?usage: resolve-api-image.sh <dev|prod>}"
PLACEHOLDER="mcr.microsoft.com/k8se/quickstart:latest"
RESOURCE_GROUP="rg-foundrygate-${FG_ENV}"
CONTAINER_APP="ca-foundrygate-api-${FG_ENV}"

if [ "$(az group exists --name "$RESOURCE_GROUP" -o tsv)" != "true" ]; then
  echo "::notice::Resource group ${RESOURCE_GROUP} does not exist yet — bootstrap run, using placeholder image ${PLACEHOLDER}" >&2
  echo "$PLACEHOLDER"
  exit 0
fi

stderr_file="$(mktemp)"
trap 'rm -f "$stderr_file"' EXIT

if image="$(az containerapp show --name "$CONTAINER_APP" --resource-group "$RESOURCE_GROUP" \
  --query 'properties.template.containers[0].image' -o tsv 2>"$stderr_file")"; then
  if [ -z "$image" ]; then
    echo "::error::${CONTAINER_APP} exists but reports no container image — refusing to guess." >&2
    exit 1
  fi
  echo "::notice::${CONTAINER_APP} currently runs ${image}; passing it as FG_API_IMAGE" >&2
  echo "$image"
  exit 0
fi

if grep -qiE "ResourceNotFound|could not be found|does not exist|was not found" "$stderr_file"; then
  echo "::notice::${CONTAINER_APP} not found in ${RESOURCE_GROUP} — bootstrap run, using placeholder image ${PLACEHOLDER}" >&2
  echo "$PLACEHOLDER"
  exit 0
fi

echo "::error::Could not read the current image of ${CONTAINER_APP} (not a not-found error). Refusing to fall back to the placeholder — that would reset the running API." >&2
cat "$stderr_file" >&2
exit 1
