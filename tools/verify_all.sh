#!/usr/bin/env bash
# ---------------------------------------------------------------------------------------
# Run every offline verification step, in the order CI runs them.
#
#   1. :core behavioural suite          (kotlinc + JRE only)
#   2. Android resource/manifest check  (python3 only)
#   3. :app type-check against android.jar + app suite
#   4. GitHub Actions workflow check    (python3 + PyYAML; skips without PyYAML)
#
# None of these need Gradle, the Android SDK, or a network connection once the Kotlin
# compiler and android.jar are cached.
# ---------------------------------------------------------------------------------------
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

FAILED=()

run_step() {
  local title="$1"
  shift
  echo
  echo "################################################################"
  echo "# $title"
  echo "################################################################"
  if "$@"; then
    echo "==> PASS: $title"
  else
    echo "==> FAIL: $title"
    FAILED+=("$title")
  fi
}

run_step "core behavioural suite" ./tools/local_core_check.sh
run_step "android resource validation" python3 tools/validate_android.py --verbose
run_step "android type-check + app suite" ./tools/local_android_check.sh
run_step "workflow validation" python3 tools/validate_workflow.py

echo
echo "================================================================"
if [[ ${#FAILED[@]} -eq 0 ]]; then
  echo "ALL OFFLINE CHECKS PASSED"
  echo "================================================================"
  exit 0
else
  echo "FAILED STEP(S):"
  for step in "${FAILED[@]}"; do echo "  - $step"; done
  echo "================================================================"
  exit 1
fi
