#!/usr/bin/env bash
# Reads the outputs contract of the subscription-scope deployment (docs reference/infrastructure
# "Outputs contract") and exports them as step outputs (kebab-case) AND environment variables
# (SCREAMING_SNAKE, prefixed FG_) for the rest of the job. The code-deploy workflows resolve
# resource names from here instead of hard-coded GitHub variables (#69 comment).
#
# Usage: export-outputs.sh <deployment-name>      # e.g. foundrygate-dev
set -euo pipefail

DEPLOYMENT_NAME="${1:?usage: export-outputs.sh <deployment-name>}"

if ! outputs_json="$(az deployment sub show --name "$DEPLOYMENT_NAME" --query properties.outputs -o json 2>/dev/null)" \
   || [ -z "$outputs_json" ] || [ "$outputs_json" = "null" ]; then
  echo "::error title=Infra deployment not found::Subscription deployment '${DEPLOYMENT_NAME}' has no outputs. Run infra-deploy.yml (or deploy-all.yml) for this environment first — the code-deploy workflows read resource names from its outputs." >&2
  exit 1
fi

# output name in Bicep -> step output name -> env var name
declare -a MAP=(
  "resourceGroupName resource-group FG_RESOURCE_GROUP"
  "apimName apim-name FG_APIM_NAME"
  "apimGatewayUrl apim-gateway-url FG_APIM_GATEWAY_URL"
  "keyVaultName key-vault-name FG_KEY_VAULT_NAME"
  "controlPlaneDeployed control-plane-deployed FG_CONTROL_PLANE_DEPLOYED"
  "containerAppIsBootstrapImage container-app-is-bootstrap-image FG_CONTAINER_APP_IS_BOOTSTRAP_IMAGE"
  "sqlServerName sql-server-name FG_SQL_SERVER_NAME"
  "sqlDatabaseName sql-database-name FG_SQL_DATABASE_NAME"
  "containerRegistryName container-registry-name FG_CONTAINER_REGISTRY_NAME"
  "containerRegistryLoginServer container-registry-login-server FG_CONTAINER_REGISTRY_LOGIN_SERVER"
  "containerAppName container-app-name FG_CONTAINER_APP_NAME"
  "containerAppFqdn container-app-fqdn FG_CONTAINER_APP_FQDN"
  "functionAppName function-app-name FG_FUNCTION_APP_NAME"
  "functionAppHostname function-app-hostname FG_FUNCTION_APP_HOSTNAME"
  "staticWebAppName static-web-app-name FG_STATIC_WEB_APP_NAME"
  "staticWebAppHostname static-web-app-hostname FG_STATIC_WEB_APP_HOSTNAME"
  "apiIdentityName api-identity-name FG_API_IDENTITY_NAME"
  "apiIdentityClientId api-identity-client-id FG_API_IDENTITY_CLIENT_ID"
  "functionsIdentityName functions-identity-name FG_FUNCTIONS_IDENTITY_NAME"
  "functionsIdentityClientId functions-identity-client-id FG_FUNCTIONS_IDENTITY_CLIENT_ID"
)

{
  echo "### Infra outputs (\`${DEPLOYMENT_NAME}\`)"
  echo ""
  echo "| Output | Value |"
  echo "|---|---|"
} >> "${GITHUB_STEP_SUMMARY:-/dev/null}"

for entry in "${MAP[@]}"; do
  read -r bicep_name output_name env_name <<<"$entry"
  value="$(jq -r --arg k "$bicep_name" '.[$k].value // "" | tostring' <<<"$outputs_json")"
  echo "${output_name}=${value}" >> "${GITHUB_OUTPUT:-/dev/null}"
  echo "${env_name}=${value}" >> "${GITHUB_ENV:-/dev/null}"
  echo "| \`${bicep_name}\` | \`${value}\` |" >> "${GITHUB_STEP_SUMMARY:-/dev/null}"
done

echo "deployment-name=${DEPLOYMENT_NAME}" >> "${GITHUB_OUTPUT:-/dev/null}"
echo "" >> "${GITHUB_STEP_SUMMARY:-/dev/null}"
