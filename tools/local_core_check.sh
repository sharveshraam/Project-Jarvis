#!/usr/bin/env bash
# ---------------------------------------------------------------------------------------
# Offline core verification.
#
# Compiles :core (pure Kotlin, ZERO dependencies) and runs its complete behavioural test
# suite using only:
#
#   * a Kotlin compiler   (kotlinc)
#   * a Java runtime      (java, JRE is enough - no javac required)
#
# No Android SDK. No Gradle. No network. No Maven repository access.
#
# This exists because the project's central claim is that the assistant's brain is
# platform-independent and locally testable. If this script stops passing, the claim
# is false, so CI runs it as its very first job.
#
# Usage:
#   ./tools/local_core_check.sh
#
# Environment overrides:
#   KOTLINC=/path/to/kotlinc     explicit compiler executable
#   KOTLIN_HOME=/path/to/kotlinc-dist   directory containing bin/kotlinc
#   JAVA_HOME=/path/to/jdk       explicit Java runtime
# ---------------------------------------------------------------------------------------
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

OUT_DIR="build/local-core"
MAIN_CLASSES="$OUT_DIR/classes"
TEST_CLASSES="$OUT_DIR/test-classes"

# ---------------------------------------------------------------- locate kotlinc -------
find_kotlinc() {
  if [[ -n "${KOTLINC:-}" && -x "$KOTLINC" ]]; then echo "$KOTLINC"; return 0; fi
  if [[ -n "${KOTLIN_HOME:-}" && -x "$KOTLIN_HOME/bin/kotlinc" ]]; then echo "$KOTLIN_HOME/bin/kotlinc"; return 0; fi
  if command -v kotlinc >/dev/null 2>&1; then command -v kotlinc; return 0; fi
  # Sandbox convention used by this project's own tooling.
  local candidate="/tmp/kc/node_modules/kotlin-compiler/bin/kotlinc"
  if [[ -x "$candidate" ]]; then echo "$candidate"; return 0; fi
  return 1
}

if ! KOTLINC_BIN="$(find_kotlinc)"; then
  cat >&2 <<'EOF'
ERROR: no Kotlin compiler found.

Install one and re-run, e.g.:
  curl -fsSL -o /tmp/kotlinc.zip \
    https://github.com/JetBrains/kotlin/releases/download/v2.0.21/kotlin-compiler-2.0.21.zip
  unzip -q /tmp/kotlinc.zip -d /tmp/kotlinc-dist
  KOTLIN_HOME=/tmp/kotlinc-dist/kotlinc ./tools/local_core_check.sh
EOF
  exit 1
fi

# ------------------------------------------------------------------- locate java -------
find_java() {
  if [[ -n "${JAVA_HOME:-}" && -x "$JAVA_HOME/bin/java" ]]; then echo "$JAVA_HOME/bin/java"; return 0; fi
  if command -v java >/dev/null 2>&1; then command -v java; return 0; fi
  # A JRE installed from PyPI (jdk4py) - a JRE is sufficient, we never need javac.
  local candidate
  candidate="$(python3 -c 'import jdk4py;print(jdk4py.JAVA)' 2>/dev/null || true)"
  if [[ -n "$candidate" && -x "$candidate" ]]; then echo "$candidate"; return 0; fi
  return 1
}

if ! JAVA_BIN="$(find_java)"; then
  echo "ERROR: no Java runtime found. Set JAVA_HOME or install a JRE." >&2
  exit 1
fi

# kotlinc needs to know where the JVM lives.
if [[ -z "${JAVA_HOME:-}" ]]; then
  export JAVA_HOME="$(dirname "$(dirname "$JAVA_BIN")")"
fi

echo "==> kotlinc : $KOTLINC_BIN"
echo "==> java    : $JAVA_BIN"
echo "==> output  : $OUT_DIR"

rm -rf "$OUT_DIR"
mkdir -p "$MAIN_CLASSES" "$TEST_CLASSES"

# ---------------------------------------------------------------- compile :core main ---
mapfile -t MAIN_SOURCES < <(find core/src/main/kotlin -name '*.kt' | sort)
if [[ ${#MAIN_SOURCES[@]} -eq 0 ]]; then
  echo "ERROR: no Kotlin sources under core/src/main/kotlin" >&2
  exit 1
fi

echo "==> compiling ${#MAIN_SOURCES[@]} core source files"
"$KOTLINC_BIN" \
  -nowarn \
  -jvm-target 17 \
  -d "$MAIN_CLASSES" \
  "${MAIN_SOURCES[@]}"

# ---------------------------------------------------------------- compile :core tests --
# *JUnit.kt files are thin bridges for Gradle/CI and reference junit:junit, which is not
# available offline. They are excluded here on purpose; the suite they delegate to is
# the one being compiled and executed below.
mapfile -t TEST_SOURCES < <(find core/src/test/kotlin -name '*.kt' ! -name '*JUnit*' | sort)
if [[ ${#TEST_SOURCES[@]} -eq 0 ]]; then
  echo "ERROR: no offline test sources under core/src/test/kotlin" >&2
  exit 1
fi

echo "==> compiling ${#TEST_SOURCES[@]} test source files"
# -Xfriend-paths makes `internal` declarations in :core visible to its own test sources, which is
# exactly what the Kotlin Gradle plugin does for a test source set. Without it the offline runner
# would be stricter than `./gradlew :core:test`, and suites could not exercise internal
# collaborators such as the NLU rule table.
"$KOTLINC_BIN" \
  -nowarn \
  -jvm-target 17 \
  -cp "$MAIN_CLASSES" \
  -Xfriend-paths="$MAIN_CLASSES" \
  -d "$TEST_CLASSES" \
  "${TEST_SOURCES[@]}"

# ------------------------------------------------------------------------- run tests ---
KOTLIN_STDLIB="$(dirname "$(dirname "$KOTLINC_BIN")")/lib/kotlin-stdlib.jar"
if [[ ! -f "$KOTLIN_STDLIB" ]]; then
  echo "ERROR: could not locate kotlin-stdlib.jar next to $KOTLINC_BIN" >&2
  exit 1
fi

echo "==> running dev.jarvis.core.testing.LocalTestMainKt"
echo
"$JAVA_BIN" -cp "$TEST_CLASSES:$MAIN_CLASSES:$KOTLIN_STDLIB" \
  dev.jarvis.core.testing.LocalTestMainKt
