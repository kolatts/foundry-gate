#!/usr/bin/env bash
# Local, offline test for resolve-api-image.sh — the one script whose failure mode is "the
# production API quietly becomes the k8se quickstart page". It stubs the Azure CLI through the
# FG_AZ hook and asserts, for every branch, both the exit code and the value written to
# $GITHUB_OUTPUT (the caller reads the image from there, never from a command substitution).
#
#   bash .github/scripts/infra/resolve-api-image.test.sh
#
# Requires: bash, jq. No Azure, no network, no credentials.
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TARGET="${SCRIPT_DIR}/resolve-api-image.sh"
PLACEHOLDER="mcr.microsoft.com/k8se/quickstart:latest"
REAL_IMAGE="crfoundrygatedeve7k2.azurecr.io/foundrygate-api:0.3.7-abc1234"

workdir="$(mktemp -d)"
trap 'rm -rf "$workdir"' EXIT
pass=0
fail=0

# Writes a stub `az` whose behaviour per subcommand is driven by the case's environment.
make_stub() {
  cat >"$workdir/az-stub" <<'STUB'
#!/usr/bin/env bash
case "$1 $2" in
  "deployment sub")
    [ -n "${STUB_DEPLOYMENT_ERROR:-}" ] && { echo "$STUB_DEPLOYMENT_ERROR" >&2; exit 1; }
    printf '%s' "${STUB_DEPLOYMENT_OUTPUTS:-null}"
    ;;
  "containerapp show")
    [ -n "${STUB_CONTAINERAPP_ERROR:-}" ] && { echo "$STUB_CONTAINERAPP_ERROR" >&2; exit 1; }
    printf '%s\n' "${STUB_CONTAINERAPP_IMAGE-}"
    ;;
  *)
    echo "unexpected stub call: $*" >&2; exit 99 ;;
esac
STUB
  chmod +x "$workdir/az-stub"
}

# run_case <name> <expected-exit> <expected-image-or-empty>
run_case() {
  local name="$1" want_exit="$2" want_image="$3"
  local out="$workdir/github_output"
  : >"$out"
  local stdout_file="$workdir/stdout" stderr_file="$workdir/stderr"

  GITHUB_OUTPUT="$out" FG_AZ="$workdir/az-stub" \
    bash "$TARGET" dev foundrygate-dev >"$stdout_file" 2>"$stderr_file"
  local got_exit=$?
  local got_image
  got_image="$(sed -n 's/^image=//p' "$out")"

  if [ "$got_exit" = "$want_exit" ] && [ "$got_image" = "$want_image" ]; then
    printf 'ok   %-52s exit=%s image=%s\n' "$name" "$got_exit" "${got_image:-<none>}"
    pass=$((pass + 1))
  else
    printf 'FAIL %-52s exit=%s (want %s) image=%s (want %s)\n' \
      "$name" "$got_exit" "$want_exit" "${got_image:-<none>}" "${want_image:-<none>}"
    sed 's/^/       stderr: /' "$stderr_file"
    fail=$((fail + 1))
  fi
}

make_stub

# 1. Day 0: the deployment record does not exist at all -> placeholder, success.
STUB_DEPLOYMENT_ERROR="ERROR: (DeploymentNotFound) Deployment 'foundrygate-dev' could not be found." \
  run_case "day 0 — no deployment record" 0 "$PLACEHOLDER"

# 2. Deployment exists but has no outputs yet -> placeholder, success.
STUB_DEPLOYMENT_OUTPUTS="null" \
  run_case "deployment exists, outputs null" 0 "$PLACEHOLDER"

# 3. Outputs exist but the control plane half was not deployed -> placeholder, success.
STUB_DEPLOYMENT_OUTPUTS='{"containerAppName":{"value":""},"resourceGroupName":{"value":""}}' \
  run_case "control plane not deployed" 0 "$PLACEHOLDER"

# 4. Resource group known, container app not created yet -> placeholder, success.
STUB_DEPLOYMENT_OUTPUTS='{"containerAppName":{"value":""},"resourceGroupName":{"value":"rg-foundrygate-dev"}}' \
  run_case "rg known, no container app yet" 0 "$PLACEHOLDER"

# 5. Happy path: the app reports its current image -> that image, success.
STUB_DEPLOYMENT_OUTPUTS='{"containerAppName":{"value":"ca-foundrygate-api-dev"},"resourceGroupName":{"value":"rg-foundrygate-dev"}}' \
STUB_CONTAINERAPP_IMAGE="$REAL_IMAGE" \
  run_case "running app — current image is reused" 0 "$REAL_IMAGE"

# 6. THE regression: an auth failure on the deployment read must NOT look like day 0.
STUB_DEPLOYMENT_ERROR="ERROR: AADSTS700213: No matching federated identity record found." \
  run_case "auth failure reading deployment — fatal" 1 ""

# 7. Throttling on the deployment read -> fatal.
STUB_DEPLOYMENT_ERROR="ERROR: (TooManyRequests) The request is being throttled." \
  run_case "throttled reading deployment — fatal" 1 ""

# 8. Deployment names the app but reading it fails (rg deleted behind the record) -> fatal.
STUB_DEPLOYMENT_OUTPUTS='{"containerAppName":{"value":"ca-foundrygate-api-dev"},"resourceGroupName":{"value":"rg-foundrygate-dev"}}' \
STUB_CONTAINERAPP_ERROR="ERROR: (ResourceGroupNotFound) Resource group 'rg-foundrygate-dev' could not be found." \
  run_case "app named but unreadable — fatal" 1 ""

# 9. Auth failure reading the app -> fatal.
STUB_DEPLOYMENT_OUTPUTS='{"containerAppName":{"value":"ca-foundrygate-api-dev"},"resourceGroupName":{"value":"rg-foundrygate-dev"}}' \
STUB_CONTAINERAPP_ERROR="ERROR: AuthorizationFailed" \
  run_case "auth failure reading app — fatal" 1 ""

# 10. App exists but reports no image -> fatal, refuse to guess.
STUB_DEPLOYMENT_OUTPUTS='{"containerAppName":{"value":"ca-foundrygate-api-dev"},"resourceGroupName":{"value":"rg-foundrygate-dev"}}' \
STUB_CONTAINERAPP_IMAGE="" \
  run_case "app reports no image — fatal" 1 ""

# 11. containerAppName without resourceGroupName -> fatal, refuse to guess the group.
STUB_DEPLOYMENT_OUTPUTS='{"containerAppName":{"value":"ca-foundrygate-api-dev"},"resourceGroupName":{"value":""}}' \
  run_case "app without resource group — fatal" 1 ""

echo
echo "${pass} passed, ${fail} failed"
[ "$fail" -eq 0 ]
