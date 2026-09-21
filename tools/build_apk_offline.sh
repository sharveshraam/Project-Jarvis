#!/usr/bin/env bash
# ---------------------------------------------------------------------------------------
# Build a real, installable debug APK with NO Gradle, NO Android Studio and NO network
# beyond the one-time tool downloads described below.
#
#   ./tools/build_apk_offline.sh
#
# Why this exists
# ---------------
# The authoritative build is `./gradlew :app:assembleDebug` (locally) or the GitHub
# Actions workflow `.github/workflows/build-apk.yml` (in CI). Both need to resolve AGP and
# the Android SDK from Google's Maven/SDK repositories. Where those hosts are unreachable
# - a sandboxed CI box, an air-gapped network, this repository's own dev environment - this
# script performs the same five transformations AGP would, using the same inputs:
#
#   1. aapt2 compile + link   resources/  -> base.apk (resources.arsc, res/, manifest) + R.java
#   2. kotlinc                :core + :app + the generated R -> JVM class files
#   3. d8                     class files + kotlin-stdlib -> classes.dex
#   4. zipalign               resources.arsc stored uncompressed and 4-byte aligned
#   5. apksigner              v1 + v2 signature with a locally generated debug key
#
# It is a faithful build of THIS project's sources, but it is not AGP: there is no manifest
# merger (this project has exactly one manifest, and the two relative class names are
# expanded here the same way the merger would), no BuildConfig generation (nothing reads
# it), no resource shrinking or R8 minification (debug builds disable both anyway), and no
# lint. `./gradlew :app:assembleDebug` remains the reference.
#
# Tool provenance (recorded because it matters for trust)
# -------------------------------------------------------
#   * android.jar API 35  - tools/local_android_check.sh already fetches and caches this
#                           from the Sable/android-platforms mirror of the public platform
#                           jars. Compile-time contract only.
#   * aapt2, d8, apksigner, zipalign - Android SDK build-tools 35.0.0, the exact version
#                           the CI workflow installs via sdkmanager. Set AAPT2 / D8_JAR /
#                           APKSIGNER_JAR / ZIPALIGN to point at your own SDK instead:
#                             AAPT2=$ANDROID_HOME/build-tools/35.0.0/aapt2 ./tools/build_apk_offline.sh
#                           Nothing is downloaded by this script; it only uses what is
#                           already on disk (see BUILD_TOOLS_DIR below).
#   * kotlinc, kotlin-stdlib - the version named in gradle/libs.versions.toml.
#
# The signing key is generated fresh into build/ (gitignored) and is a throwaway debug key:
# the produced APK installs on a device but must never be published.
# ---------------------------------------------------------------------------------------
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

OUT_DIR="build/apk-offline"
# Final artifact. Deliberately NOT under build/, dist/ or out/: those are commonly excluded
# from workspace snapshots and artifact retention, and this file is meant to be collected.
APK_DIR="apk"

# ----------------------------------------------------------------- project coordinates ---
# Everything here is read out of the build scripts so this cannot drift from them.
APPLICATION_ID="$(sed -n 's/^[[:space:]]*applicationId[[:space:]]*=[[:space:]]*"\([^"]*\)".*/\1/p' app/build.gradle.kts | head -n 1)"
NAMESPACE="$(sed -n 's/^[[:space:]]*namespace[[:space:]]*=[[:space:]]*"\([^"]*\)".*/\1/p' app/build.gradle.kts | head -n 1)"
COMPILE_SDK="$(sed -n 's/^[[:space:]]*compileSdk[[:space:]]*=[[:space:]]*\([0-9][0-9]*\).*/\1/p' app/build.gradle.kts | head -n 1)"
MIN_SDK="$(sed -n 's/^[[:space:]]*minSdk[[:space:]]*=[[:space:]]*\([0-9][0-9]*\).*/\1/p' app/build.gradle.kts | head -n 1)"
TARGET_SDK="$(sed -n 's/^[[:space:]]*targetSdk[[:space:]]*=[[:space:]]*\([0-9][0-9]*\).*/\1/p' app/build.gradle.kts | head -n 1)"
VERSION_CODE="$(sed -n 's/^[[:space:]]*versionCode[[:space:]]*=[[:space:]]*\([0-9][0-9]*\).*/\1/p' app/build.gradle.kts | head -n 1)"
VERSION_NAME="$(sed -n 's/^[[:space:]]*versionName[[:space:]]*=[[:space:]]*"\([^"]*\)".*/\1/p' app/build.gradle.kts | head -n 1)"
# The debug build type in app/build.gradle.kts adds these two suffixes.
APP_ID_SUFFIX="$(sed -n 's/^[[:space:]]*applicationIdSuffix[[:space:]]*=[[:space:]]*"\([^"]*\)".*/\1/p' app/build.gradle.kts | head -n 1)"
VERSION_NAME_SUFFIX="$(sed -n 's/^[[:space:]]*versionNameSuffix[[:space:]]*=[[:space:]]*"\([^"]*\)".*/\1/p' app/build.gradle.kts | head -n 1)"
for var in APPLICATION_ID NAMESPACE COMPILE_SDK MIN_SDK TARGET_SDK VERSION_CODE VERSION_NAME; do
  if [[ -z "${!var}" ]]; then echo "ERROR: could not read $var from app/build.gradle.kts" >&2; exit 1; fi
done
DEBUG_APP_ID="${APPLICATION_ID}${APP_ID_SUFFIX}"
DEBUG_VERSION_NAME="${VERSION_NAME}${VERSION_NAME_SUFFIX}"

# -------------------------------------------------------------------------- locate java ---
find_java() {
  if [[ -n "${JAVA_HOME:-}" && -x "$JAVA_HOME/bin/java" ]]; then echo "$JAVA_HOME/bin/java"; return 0; fi
  if command -v java >/dev/null 2>&1; then command -v java; return 0; fi
  echo "ERROR: no Java runtime found. Set JAVA_HOME." >&2
  return 1
}
JAVA_BIN="$(find_java)"
export JAVA_HOME="${JAVA_HOME:-$(dirname "$(dirname "$JAVA_BIN")")}"

# ----------------------------------------------------------------------- locate kotlinc ---
find_kotlinc() {
  if [[ -n "${KOTLINC:-}" && -x "$KOTLINC" ]]; then echo "$KOTLINC"; return 0; fi
  if [[ -n "${KOTLIN_HOME:-}" && -x "$KOTLIN_HOME/bin/kotlinc" ]]; then echo "$KOTLIN_HOME/bin/kotlinc"; return 0; fi
  if command -v kotlinc >/dev/null 2>&1; then command -v kotlinc; return 0; fi
  local candidate="/tmp/kc/node_modules/kotlin-compiler/bin/kotlinc"
  if [[ -x "$candidate" ]]; then echo "$candidate"; return 0; fi
  echo "ERROR: no Kotlin compiler found. Set KOTLINC or KOTLIN_HOME." >&2
  return 1
}
KOTLINC_BIN="$(find_kotlinc)"
KOTLIN_LIB="$(dirname "$(dirname "$KOTLINC_BIN")")/lib"
KOTLIN_STDLIB="$KOTLIN_LIB/kotlin-stdlib.jar"
if [[ ! -f "$KOTLIN_STDLIB" ]]; then
  echo "ERROR: kotlin-stdlib.jar not found next to $KOTLINC_BIN" >&2; exit 1
fi

# -------------------------------------------------------------------- locate build-tools ---
BUILD_TOOLS_DIR="${BUILD_TOOLS_DIR:-.tools-cache/build-tools/35.0.0}"
AAPT2="${AAPT2:-$BUILD_TOOLS_DIR/aapt2}"
ZIPALIGN="${ZIPALIGN:-$BUILD_TOOLS_DIR/zipalign}"
D8_JAR="${D8_JAR:-$BUILD_TOOLS_DIR/lib/d8.jar}"
APKSIGNER_JAR="${APKSIGNER_JAR:-$BUILD_TOOLS_DIR/lib/apksigner.jar}"
KEYTOOL="${KEYTOOL:-$JAVA_HOME/bin/keytool}"
for tool in "$AAPT2" "$ZIPALIGN" "$D8_JAR" "$APKSIGNER_JAR" "$KEYTOOL"; do
  if [[ ! -e "$tool" ]]; then
    echo "ERROR: missing build tool: $tool" >&2
    echo "       Set BUILD_TOOLS_DIR to an Android SDK build-tools 35.0.0 directory," >&2
    echo "       e.g. \$ANDROID_HOME/build-tools/35.0.0" >&2
    exit 1
  fi
done

# ---------------------------------------------------------------------- locate android.jar -
ANDROID_JAR="${ANDROID_JAR:-.tools-cache/android-$COMPILE_SDK/android.jar}"
if [[ ! -f "$ANDROID_JAR" ]]; then
  echo "ERROR: android.jar for API $COMPILE_SDK not found at $ANDROID_JAR" >&2
  echo "       Run ./tools/local_android_check.sh once to fetch and cache it," >&2
  echo "       or set ANDROID_JAR=\$ANDROID_HOME/platforms/android-$COMPILE_SDK/android.jar" >&2
  exit 1
fi

echo "==> applicationId : $DEBUG_APP_ID   (namespace $NAMESPACE)"
echo "==> version       : $DEBUG_VERSION_NAME ($VERSION_CODE)"
echo "==> sdk           : min $MIN_SDK / target $TARGET_SDK / compile $COMPILE_SDK"
echo "==> aapt2         : $("$AAPT2" version 2>&1 | head -n 1)"
echo "==> kotlinc       : $KOTLINC_BIN"
echo "==> android.jar   : $ANDROID_JAR"

rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR"/{gen,classes,dex} "$APK_DIR"

# =========================================================================== 1. manifest ==
# app/src/main/AndroidManifest.xml declares no package attribute (AGP takes it from
# applicationId) and uses relative class names (AGP expands them against the namespace
# during the merge). Both are resolved here so aapt2 and the device agree on names.
echo "==> preparing the build manifest"
python3 - "$NAMESPACE" "$DEBUG_APP_ID" "$OUT_DIR/AndroidManifest.xml" <<'PY'
import re, sys
namespace, app_id, dest = sys.argv[1:4]
src = open("app/src/main/AndroidManifest.xml", encoding="utf-8").read()
if "package=" in src.split(">", 1)[0]:
    sys.exit("ERROR: the manifest already declares package=; this transform would be wrong")
src = src.replace("<manifest ", f'<manifest package="{app_id}" ', 1)
# ".Foo" and ".foo.Bar" are relative to the *namespace*, not to the applicationId.
def expand(match):
    # group(1) already ends with the opening quote; group(2) is ".Foo".
    return f'{match.group(1)}{namespace}{match.group(2)}"'
src, count = re.subn(r'(android:name=")(\.[A-Za-z_][\w.]*)"', expand, src)
open(dest, "w", encoding="utf-8").write(src)
print(f"    package=\"{app_id}\", expanded {count} relative class name(s)")
PY

# ============================================================================ 2. aapt2 ====
echo "==> aapt2 compile (resources)"
"$AAPT2" compile --dir app/src/main/res -o "$OUT_DIR/res-compiled.zip"

# --debug-mode sets android:debuggable="true", which is what AGP's debug build type does.
echo "==> aapt2 link (base APK + R.java)"
"$AAPT2" link \
  -o "$OUT_DIR/base.apk" \
  -I "$ANDROID_JAR" \
  --manifest "$OUT_DIR/AndroidManifest.xml" \
  --min-sdk-version "$MIN_SDK" \
  --target-sdk-version "$TARGET_SDK" \
  --version-code "$VERSION_CODE" \
  --version-name "$DEBUG_VERSION_NAME" \
  --custom-package "$NAMESPACE" \
  --java "$OUT_DIR/gen" \
  --auto-add-overlay \
  --debug-mode \
  "$OUT_DIR/res-compiled.zip"

R_JAVA="$OUT_DIR/gen/$(echo "$NAMESPACE" | tr . /)/R.java"
if [[ ! -f "$R_JAVA" ]]; then echo "ERROR: aapt2 did not generate $R_JAVA" >&2; exit 1; fi

# aapt2 emits R.java, and there is no javac in this environment, so the resource table is
# re-expressed as Kotlin. The ids are aapt2's real ones, not fabricated: an R class whose
# ids disagree with resources.arsc would compile and then crash at the first findViewById.
echo "==> R.java -> R.kt (real resource ids)"
python3 - "$R_JAVA" "$OUT_DIR/gen/R.kt" <<'PY'
import re, sys
src_path, dest = sys.argv[1:3]
src = open(src_path, encoding="utf-8").read()
package = re.search(r"^package\s+([\w.]+);", src, re.M).group(1)
KEYWORDS = {"object", "class", "fun", "val", "var", "in", "is", "as", "if", "else", "when",
            "for", "while", "do", "return", "this", "super", "package", "import", "null",
            "true", "false", "typeof", "interface", "enum", "sealed", "data", "companion"}
def safe(name):
    return f"`{name}`" if name in KEYWORDS else name
out = ["// Generated by tools/build_apk_offline.sh from aapt2's R.java.",
       "// The ids below are the real ids in this APK's resources.arsc.",
       f"package {package}", "", "object R {"]
inner = None
for raw in src.splitlines():
    line = raw.strip()
    m = re.match(r"public static final class (\w+) \{", line)
    if m:
        inner = m.group(1)
        out.append(f"    object {safe(inner)} {{")
        continue
    if line == "}":
        if inner is None:
            break
        out.append("    }")
        inner = None
        continue
    if inner is None:
        continue
    m = re.match(r"public static final int\[\]\s+(\w+)\s*=\s*new int\[\]\s*\{\s*(.*?)\s*\};", line)
    if m:
        values = ", ".join(v.strip() for v in m.group(2).split(",") if v.strip())
        out.append(f"        val {safe(m.group(1))}: IntArray = intArrayOf({values})")
        continue
    m = re.match(r"public static final int\s+(\w+)\s*=\s*(-?0x[0-9a-fA-F]+|-?\d+);", line)
    if m:
        out.append(f"        const val {safe(m.group(1))}: Int = {m.group(2)}")
        continue
out.append("}")
open(dest, "w", encoding="utf-8").write("\n".join(out) + "\n")
entries = sum(1 for l in out if "const val" in l or "IntArray" in l)
if entries == 0:
    # Emitting an empty R would turn into a wall of "unresolved reference" errors from
    # kotlinc, far from the real cause. Fail here instead.
    sys.exit(f"ERROR: parsed no resource ids out of {src_path}; R.java format is not what "
             f"this converter expects")
print(f"    {entries} resource id(s) in {len(out)} lines")
PY

# =========================================================================== 3. kotlinc ===
mapfile -t SOURCES < <(
  {
    find core/src/main/kotlin -name '*.kt'
    find app/src/main/kotlin -name '*.kt'
    echo "$OUT_DIR/gen/R.kt"
  } | sort
)
echo "==> kotlinc: ${#SOURCES[@]} source files (:core + :app + generated R)"
# -no-jdk: android.jar supplies the java.* surface an APK may use. -Xjvm-default=all
# matches core/build.gradle.kts.
"$KOTLINC_BIN" \
  -nowarn \
  -no-jdk \
  -jvm-target 17 \
  -Xjvm-default=all \
  -cp "$ANDROID_JAR" \
  -d "$OUT_DIR/app-classes.jar" \
  "${SOURCES[@]}"

# ================================================================================ 4. d8 ===
echo "==> d8: dexing app classes + kotlin-stdlib"
D8_ARGS=(--min-api "$MIN_SDK" --lib "$ANDROID_JAR" --output "$OUT_DIR/dex")
# org.jetbrains.annotations is referenced by Kotlin-generated code with CLASS retention;
# giving it to d8 as a classpath entry silences the missing-class warning without dexing it.
if compgen -G "$KOTLIN_LIB/annotations-*.jar" > /dev/null; then
  D8_ARGS+=(--classpath "$(ls "$KOTLIN_LIB"/annotations-*.jar | head -n 1)")
fi
"$JAVA_BIN" -cp "$D8_JAR" com.android.tools.r8.D8 \
  "${D8_ARGS[@]}" \
  "$OUT_DIR/app-classes.jar" \
  "$KOTLIN_STDLIB"
DEX_FILES=("$OUT_DIR"/dex/classes*.dex)
if [[ ! -f "${DEX_FILES[0]}" ]]; then echo "ERROR: d8 produced no classes.dex" >&2; exit 1; fi
echo "    $(printf '%s ' "${DEX_FILES[@]#"build/apk-offline/dex/"}")"

# ========================================================================= 5. assemble ====
echo "==> assembling unsigned APK"
python3 - "$OUT_DIR/base.apk" "$OUT_DIR/dex" "$OUT_DIR/unsigned.apk" <<'PY'
import pathlib, sys, zipfile
base, dex_dir, dest = sys.argv[1:4]
dexes = sorted(pathlib.Path(dex_dir).glob("classes*.dex"))
if not dexes:
    sys.exit("ERROR: no classes.dex to add")
with zipfile.ZipFile(base) as src, zipfile.ZipFile(dest, "w") as out:
    for item in src.infolist():
        data = src.read(item.filename)
        # Android 11+ refuses to install an APK whose resources.arsc is compressed; aapt2
        # already stores it, and zipalign below makes it 4-byte aligned.
        mode = zipfile.ZIP_STORED if item.filename == "resources.arsc" else item.compress_type
        out.writestr(zipfile.ZipInfo(item.filename, date_time=item.date_time), data, compress_type=mode)
    for dex in dexes:
        out.writestr(zipfile.ZipInfo(dex.name, date_time=(1980, 1, 1, 0, 0, 0)),
                     dex.read_bytes(), compress_type=zipfile.ZIP_DEFLATED)
    names = out.namelist()
print(f"    {len(names)} entries")
for required in ("AndroidManifest.xml", "resources.arsc", "classes.dex"):
    if required not in names:
        sys.exit(f"ERROR: {required} missing from the assembled APK")
PY

echo "==> zipalign (4-byte; resources.arsc must be stored and aligned for API 30+)"
"$ZIPALIGN" -f 4 "$OUT_DIR/unsigned.apk" "$OUT_DIR/aligned.apk"
"$ZIPALIGN" -c 4 "$OUT_DIR/aligned.apk" >/dev/null
echo "    alignment verified"

# ============================================================================= 6. sign ====
KEYSTORE="$OUT_DIR/debug.keystore"
if [[ ! -f "$KEYSTORE" ]]; then
  echo "==> generating a throwaway debug keystore"
  "$KEYTOOL" -genkeypair -v \
    -keystore "$KEYSTORE" \
    -alias androiddebugkey \
    -storepass android -keypass android \
    -keyalg RSA -keysize 2048 \
    -validity 10000 \
    -dname "CN=Android Debug,O=Android,C=US" >/dev/null 2>&1
fi

FINAL_APK="$APK_DIR/Jarvis-${DEBUG_VERSION_NAME}.apk"
echo "==> apksigner (v1 + v2)"
"$JAVA_BIN" -jar "$APKSIGNER_JAR" sign \
  --ks "$KEYSTORE" \
  --ks-key-alias androiddebugkey \
  --ks-pass pass:android --key-pass pass:android \
  --v1-signing-enabled true \
  --v2-signing-enabled true \
  --out "$FINAL_APK" \
  "$OUT_DIR/aligned.apk"

# ============================================================================ 7. prove ====
echo "==> verifying the result"
"$JAVA_BIN" -jar "$APKSIGNER_JAR" verify --verbose --print-certs "$FINAL_APK" | sed 's/^/    /'
echo "    --- badging"
"$AAPT2" dump badging "$FINAL_APK" |
  grep -E "^package:|^sdkVersion:|^targetSdkVersion:|^application-label:|^launchable-activity:" |
  sed 's/^/    /'
SIZE="$(stat -c %s "$FINAL_APK")"
SHA="$(sha256sum "$FINAL_APK" | cut -d' ' -f1)"
echo
echo "================================================================"
echo "APK      : $FINAL_APK"
echo "Size     : $SIZE bytes ($(( SIZE / 1024 )) KiB)"
echo "sha256   : $SHA"
echo "Package  : $DEBUG_APP_ID"
echo "Version  : $DEBUG_VERSION_NAME (code $VERSION_CODE)"
echo "Signed   : debug key (throwaway, generated into build/) - NOT for publication"
echo "================================================================"
