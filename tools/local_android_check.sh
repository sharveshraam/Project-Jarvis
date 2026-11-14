#!/usr/bin/env bash
# ---------------------------------------------------------------------------------------
# Offline Android type-check.
#
# Compiles :core AND :app with kotlinc against the real API 35 android.jar. This catches
# the classes of error that would otherwise only appear on a device or in a full SDK
# build:
#
#   * unresolved or misspelled framework APIs (wrong method name, wrong signature)
#   * missing abstract overrides (e.g. AccessibilityService.onAccessibilityEvent)
#   * unresolved R references, i.e. resource names that do not exist
#   * Kotlin/JVM type errors anywhere in the Android layer
#
# It does NOT and cannot:
#   * produce an APK (needs aapt2 + d8 + zipalign + apksigner from the Android SDK)
#   * execute framework code (android.jar methods throw at runtime - it is a compile-time
#     contract, exactly like the android.jar in a real SDK)
#   * validate resource *values* (that is tools/validate_android.py's job)
#
# android.jar is fetched once from a public mirror of the platform jars and cached in
# .tools-cache/ (gitignored). Point ANDROID_JAR at a real SDK platform to use that
# instead - it is the same file:
#
#   ANDROID_JAR=$ANDROID_HOME/platforms/android-35/android.jar ./tools/local_android_check.sh
# ---------------------------------------------------------------------------------------
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

API_LEVEL="${API_LEVEL:-35}"
CACHE_DIR=".tools-cache"
OUT_DIR="build/local-android"
ANDROID_JAR="${ANDROID_JAR:-$CACHE_DIR/android-$API_LEVEL/android.jar}"

# ---------------------------------------------------------------- locate kotlinc -------
find_kotlinc() {
  if [[ -n "${KOTLINC:-}" && -x "$KOTLINC" ]]; then echo "$KOTLINC"; return 0; fi
  if [[ -n "${KOTLIN_HOME:-}" && -x "$KOTLIN_HOME/bin/kotlinc" ]]; then echo "$KOTLIN_HOME/bin/kotlinc"; return 0; fi
  if command -v kotlinc >/dev/null 2>&1; then command -v kotlinc; return 0; fi
  local candidate="/tmp/kc/node_modules/kotlin-compiler/bin/kotlinc"
  if [[ -x "$candidate" ]]; then echo "$candidate"; return 0; fi
  return 1
}

if ! KOTLINC_BIN="$(find_kotlinc)"; then
  echo "ERROR: no Kotlin compiler found. Set KOTLINC or KOTLIN_HOME." >&2
  exit 1
fi

find_java() {
  if [[ -n "${JAVA_HOME:-}" && -x "$JAVA_HOME/bin/java" ]]; then echo "$JAVA_HOME/bin/java"; return 0; fi
  if command -v java >/dev/null 2>&1; then command -v java; return 0; fi
  local candidate
  candidate="$(python3 -c 'import jdk4py;print(jdk4py.JAVA)' 2>/dev/null || true)"
  if [[ -n "$candidate" && -x "$candidate" ]]; then echo "$candidate"; return 0; fi
  return 1
}

if ! JAVA_BIN="$(find_java)"; then
  echo "ERROR: no Java runtime found. Set JAVA_HOME." >&2
  exit 1
fi
export JAVA_HOME="${JAVA_HOME:-$(dirname "$(dirname "$JAVA_BIN")")}"

# ------------------------------------------------------------ obtain android.jar -------
if [[ ! -f "$ANDROID_JAR" ]]; then
  echo "==> android.jar not found at $ANDROID_JAR; fetching API $API_LEVEL platform jar"
  mkdir -p "$CACHE_DIR"
  local_clone="$CACHE_DIR/_android-platforms"
  rm -rf "$local_clone"
  # A blobless sparse clone fetches only the one file we need (~27 MB), not the
  # multi-gigabyte collection of every API level.
  git clone --depth 1 --filter=blob:none --sparse \
    https://github.com/Sable/android-platforms.git "$local_clone" >/dev/null 2>&1
  ( cd "$local_clone" && git sparse-checkout set "android-$API_LEVEL" >/dev/null 2>&1 )
  mkdir -p "$(dirname "$ANDROID_JAR")"
  cp "$local_clone/android-$API_LEVEL/android.jar" "$ANDROID_JAR"
  rm -rf "$local_clone"
fi

if [[ ! -f "$ANDROID_JAR" ]]; then
  echo "ERROR: could not obtain android.jar. Set ANDROID_JAR=/path/to/android.jar" >&2
  exit 1
fi

echo "==> kotlinc    : $KOTLINC_BIN"
echo "==> android.jar: $ANDROID_JAR"

# ------------------------------------------------------------ generate local R ---------
GEN_DIR="$OUT_DIR/gen"
rm -rf "$OUT_DIR"
mkdir -p "$GEN_DIR" "$OUT_DIR/classes"

echo "==> generating offline R class"
python3 tools/generate_local_r.py --out "$GEN_DIR"

# ------------------------------------------------------------------- compile -----------
mapfile -t SOURCES < <(
  {
    find core/src/main/kotlin -name '*.kt'
    find app/src/main/kotlin -name '*.kt'
    find "$GEN_DIR" -name '*.kt'
  } | sort
)

echo "==> compiling ${#SOURCES[@]} source files (:core + :app) against android.jar"
# -no-jdk is required: android.jar supplies the java.* surface an APK is allowed to use,
# and mixing it with a desktop JDK would let app code compile against APIs that do not
# exist on a device.
"$KOTLINC_BIN" \
  -nowarn \
  -no-jdk \
  -jvm-target 17 \
  -cp "$ANDROID_JAR" \
  -d "$OUT_DIR/classes" \
  "${SOURCES[@]}"

echo "OK: :core and :app type-check against the real Android API $API_LEVEL surface"

# ------------------------------------------------------- compile + run :app tests ------
# *JUnit.kt files are thin bridges for Gradle/CI and reference junit:junit, which is not
# reachable offline. They are excluded here on purpose; the suite they delegate to is the
# one compiled and executed below.
mapfile -t TEST_SOURCES < <(find app/src/test/kotlin -name '*.kt' ! -name '*JUnit*' | sort)
if [[ ${#TEST_SOURCES[@]} -gt 0 ]]; then
  echo
  echo "==> compiling ${#TEST_SOURCES[@]} app test source files"
  "$KOTLINC_BIN" \
    -nowarn \
    -no-jdk \
    -jvm-target 17 \
    -cp "$ANDROID_JAR:$OUT_DIR/classes" \
    -d "$OUT_DIR/test-classes" \
    "${TEST_SOURCES[@]}"

  KOTLIN_STDLIB="$(dirname "$(dirname "$KOTLINC_BIN")")/lib/kotlin-stdlib.jar"
  echo "==> running dev.jarvis.app.LocalAppTestMainKt"
  echo
  "$JAVA_BIN" \
    -cp "$OUT_DIR/test-classes:$OUT_DIR/classes:$ANDROID_JAR:$KOTLIN_STDLIB" \
    dev.jarvis.app.LocalAppTestMainKt
fi

echo
echo "OK: offline Android verification complete"
