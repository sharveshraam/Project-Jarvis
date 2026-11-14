#!/usr/bin/env python3
"""Validate the GitHub Actions workflow without running it.

GitHub only executes workflows on their own infrastructure, which makes them the one
part of this repository that cannot be verified by building locally. This script closes
that gap as far as it can be closed offline. It checks that
`.github/workflows/build-apk.yml`:

  * is syntactically valid YAML with the expected triggers, jobs and permissions;
  * pins only current majors of GitHub-maintained actions;
  * uploads exactly one artifact, named `android-debug-apk`, pointing at the single real
    APK file, with `if-no-files-found: error`;
  * builds through the committed Gradle wrapper (`./gradlew :app:assembleDebug`);
  * does not restate a Gradle/AGP/Kotlin version that conflicts with `gradle/`;
  * contains no pattern that could hide a failure (`|| true`, `continue-on-error`,
    `if: always()`), and no secrets or release-signing configuration;
  * has embedded shell that passes `bash -n`, and opts every multi-line script into
    `set -euo pipefail`.

Usage:
    python3 tools/validate_workflow.py [path/to/workflow.yml]
    python3 tools/validate_workflow.py --require-yaml      # fail instead of skip if
                                                           # PyYAML is not installed

Exit status: 0 = every check passed (or was skipped without --require-yaml), 1 = failure.
"""

from __future__ import annotations

import re
import subprocess
import sys
import tempfile
from pathlib import Path

WORKFLOW_DEFAULT = ".github/workflows/build-apk.yml"
# Current stable majors of the GitHub-maintained actions this workflow may use, verified
# against each repository's releases on 2026-09-21 (checkout v7.0.1, setup-java v6.0.1,
# upload-artifact v7.0.1). A pin below the major listed here is stale; a pin above it means
# this table needs updating, which is the point of failing loudly.
CURRENT_MAJORS = {
    "actions/checkout": 7,
    "actions/setup-java": 6,
    "actions/upload-artifact": 7,
}
BANNED_PATTERNS = [
    (r"\|\|\s*true", "`|| true` swallows a failing command"),
    (r"continue-on-error", "continue-on-error hides a failing step"),
    (r"if:\s*always\(\)", "if: always() publishes artifacts from a failed build"),
    (r"(?i)\bsecrets\.", "workflow reads a secret"),
    (r"(?i)signingConfig|keystore|storePassword|keyPassword",
     "workflow configures release signing"),
    (r"(?i)android\.builder\.sdkDownload\s*=\s*true",
     "workflow re-enables silent SDK auto-download"),
]


class Report:
    def __init__(self) -> None:
        self.rows: list[tuple[bool, str, str]] = []

    def check(self, ok: bool, label: str, detail: str = "") -> None:
        self.rows.append((bool(ok), label, detail))

    @property
    def failures(self) -> list[tuple[str, str]]:
        return [(label, detail) for ok, label, detail in self.rows if not ok]

    def emit(self) -> int:
        for ok, label, detail in self.rows:
            mark = "ok  " if ok else "FAIL"
            suffix = f" -- {detail}" if detail and not ok else ""
            print(f"[{mark}] {label}{suffix}")
        print("-" * 72)
        total = len(self.rows)
        passed = total - len(self.failures)
        print(f"{passed}/{total} workflow checks passed")
        for label, detail in self.failures:
            print(f"  FAILED: {label} {detail}".rstrip())
        return 1 if self.failures else 0


def repo_root() -> Path:
    return Path(__file__).resolve().parent.parent


def main(argv: list[str]) -> int:
    root = repo_root()
    require_yaml = "--require-yaml" in argv
    positional = [a for a in argv if not a.startswith("-")]
    workflow = root / (positional[0] if positional else WORKFLOW_DEFAULT)

    if not workflow.is_file():
        if positional:
            # An explicit path means the caller expects that file to exist.
            print(f"ERROR: workflow not found at {workflow}", file=sys.stderr)
            return 1
        # The default path is absent: this happens on a checkout where
        # .github/workflows/ has not landed yet (the automation token used on this
        # repository cannot push there). Skipping keeps ./tools/verify_all.sh usable
        # instead of failing on a file it was never able to check.
        print(f"SKIPPED: no workflow at {workflow} - nothing to validate. Pass an explicit "
              f"path to treat its absence as an error.")
        return 0

    try:
        import yaml
    except ImportError:
        message = "PyYAML is not installed; cannot parse the workflow."
        if require_yaml:
            print(f"ERROR: {message} (pip install pyyaml)", file=sys.stderr)
            return 1
        print(f"SKIPPED: {message} Install it with `pip install pyyaml`, or re-run with "
              f"--require-yaml to treat this as a failure.")
        return 0

    raw = workflow.read_text()
    report = Report()

    # Executable lines only. Comment lines are prose: this workflow's own header
    # documents which patterns are banned, and that must not trip the checks below.
    code_lines = [(n, line) for n, line in enumerate(raw.splitlines(), 1)
                  if not line.lstrip().startswith("#")]

    try:
        doc = yaml.safe_load(raw)
        report.check(True, "YAML parses")
    except yaml.YAMLError as exc:
        report.check(False, "YAML parses", str(exc).splitlines()[0])
        return report.emit()

    # ---- shape -------------------------------------------------------------------
    report.check(doc.get("name") == "Build Android APK",
                 "workflow name is 'Build Android APK'", repr(doc.get("name")))

    # PyYAML parses a bare `on:` key as Python's True (YAML 1.1 boolean).
    triggers = doc.get("on", doc.get(True))
    report.check(isinstance(triggers, dict), "has triggers")
    report.check((triggers or {}).get("push", {}).get("branches") == ["main"],
                 "triggers on push to main", str((triggers or {}).get("push")))
    report.check("workflow_dispatch" in (triggers or {}), "triggers on workflow_dispatch")
    report.check(doc.get("permissions", {}).get("contents") == "read",
                 "permissions are least-privilege (contents: read)",
                 str(doc.get("permissions")))

    jobs = doc.get("jobs") or {}
    report.check(set(jobs) == {"verify", "build-apk"},
                 "jobs are exactly verify + build-apk", str(sorted(jobs)))
    report.check(jobs.get("build-apk", {}).get("needs") == "verify",
                 "build-apk depends on verify (no APK from a broken foundation)")
    for name, job in jobs.items():
        report.check(job.get("runs-on") == "ubuntu-latest", f"{name}: runs-on ubuntu-latest")
        report.check(isinstance(job.get("timeout-minutes"), int), f"{name}: has a timeout")
        report.check(bool(job.get("steps")), f"{name}: has steps")

    steps = [(name, step) for name, job in jobs.items() for step in job.get("steps", [])]

    # ---- actions -----------------------------------------------------------------
    used = {step["uses"] for _, step in steps if "uses" in step}
    unknown = sorted(u for u in used if u.split("@")[0] not in CURRENT_MAJORS)
    report.check(not unknown,
                 "only the GitHub-maintained actions this project expects are used",
                 f"got {unknown}")
    stale, ahead = [], []
    for ref in sorted(used):
        name, _, rev = ref.partition("@")
        if name not in CURRENT_MAJORS:
            continue  # already reported by the unknown-action check above
        m = re.fullmatch(r"v(\d+)", rev)
        if not m:
            stale.append(f"{ref} (pin a major version tag, not {rev!r})")
            continue
        major = int(m.group(1))
        if major < CURRENT_MAJORS[name]:
            stale.append(f"{ref} (current is v{CURRENT_MAJORS[name]})")
        elif major > CURRENT_MAJORS[name]:
            ahead.append(f"{ref} (this validator knows v{CURRENT_MAJORS[name]})")
    report.check(not stale, "no action is pinned below its current stable major", str(stale))
    report.check(not ahead,
                 "no action is pinned above the major this validator was verified against",
                 str(ahead))

    # ---- the artifact ------------------------------------------------------------
    uploads = [step for _, step in steps
               if str(step.get("uses", "")).startswith("actions/upload-artifact")]
    report.check(len(uploads) == 1, "exactly one upload-artifact step", str(len(uploads)))
    if uploads:
        with_block = uploads[0].get("with", {})
        report.check(with_block.get("name") == "android-debug-apk",
                     "artifact is named android-debug-apk", str(with_block.get("name")))
        report.check(str(with_block.get("path", "")).endswith(".apk")
                     or with_block.get("path") == "${{ env.APK_PATH }}",
                     "artifact path is a single .apk file, not a build directory",
                     str(with_block.get("path")))
        report.check(with_block.get("if-no-files-found") == "error",
                     "a missing APK fails the run (if-no-files-found: error)")
        report.check("retention-days" in with_block, "artifact retention is bounded")

    env = doc.get("env") or {}
    report.check(env.get("APK_PATH") == "app/build/outputs/apk/debug/app-debug.apk",
                 "APK_PATH is the real :app:assembleDebug output", str(env.get("APK_PATH")))
    report.check(env.get("JAVA_VERSION") == "17",
                 "JAVA_VERSION is 17 (matches compileOptions / jvmToolchain)",
                 str(env.get("JAVA_VERSION")))

    # ---- build invocation --------------------------------------------------------
    setup_java = [step for _, step in steps
                  if str(step.get("uses", "")).startswith("actions/setup-java")]
    report.check(len(setup_java) == len(jobs), "every job sets up the JDK",
                 f"{len(setup_java)} of {len(jobs)}")
    for step in setup_java:
        w = step.get("with", {})
        report.check(w.get("distribution") == "temurin", "JDK distribution is temurin")
        report.check(str(w.get("java-version")) == "${{ env.JAVA_VERSION }}",
                     "JDK version is declared once, in env")

    build_steps = [step for _, step in steps
                   if "run" in step and "assembleDebug" in step["run"]]
    report.check(len(build_steps) == 1, "exactly one step builds the APK",
                 str(len(build_steps)))
    if build_steps:
        run = build_steps[0]["run"]
        report.check("./gradlew :app:assembleDebug" in run,
                     "builds through the committed Gradle wrapper")
        report.check("--stacktrace" in run, "build failure produces a stacktrace")
    report.check(any("chmod +x gradlew" in step.get("run", "") for _, step in steps),
                 "gradlew is made executable (Linux checkout)")
    report.check(any("sdkmanager" in step.get("run", "") for _, step in steps),
                 "the Android SDK is installed explicitly "
                 "(android.builder.sdkDownload=false means Gradle cannot fetch it)")

    # ---- versions must agree with gradle/ ---------------------------------------
    props = (root / "gradle/wrapper/gradle-wrapper.properties").read_text()
    catalog = (root / "gradle/libs.versions.toml").read_text()
    app_gradle = (root / "app/build.gradle.kts").read_text()
    declared = {
        "gradle": re.search(r"gradle-([\d.]+)-bin\.zip", props).group(1),
        "agp": re.search(r'^agp = "([\d.]+)"', catalog, re.M).group(1),
        "kotlin": re.search(r'^kotlin = "([\d.]+)"', catalog, re.M).group(1),
        "compileSdk": re.search(r"compileSdk = (\d+)", app_gradle).group(1),
    }
    for tool in ("gradle", "agp", "kotlin"):
        mentioned = set(re.findall(rf"{tool}[-_ ]?v?(\d+\.\d+(?:\.\d+)?)", raw, re.I))
        conflicting = sorted(v for v in mentioned if v != declared[tool])
        report.check(not conflicting,
                     f"workflow never pins a {tool} version other than the project's "
                     f"({declared[tool]})", str(conflicting))
    report.check("platforms;android-${COMPILE_SDK}" in raw,
                 "the SDK platform is derived from compileSdk, not hardcoded")
    report.check(re.search(r"sed .*compileSdk.*app/build\.gradle\.kts", raw, re.S) is not None,
                 "compileSdk is read out of app/build.gradle.kts")
    # A hardcoded android-<N> anywhere in executable lines must equal the project's
    # compileSdk, otherwise CI silently builds against the wrong platform.
    hardcoded_platforms = sorted({
        m for _, line in code_lines for m in re.findall(r"platforms;android-(\d+)", line)
    })
    report.check(not hardcoded_platforms
                 or hardcoded_platforms == [declared["compileSdk"]],
                 f"no hardcoded platform level other than compileSdk "
                 f"({declared['compileSdk']})", str(hardcoded_platforms))

    # ---- nothing may hide a failure ---------------------------------------------
    for pattern, why in BANNED_PATTERNS:
        hits = [n for n, line in code_lines if re.search(pattern, line)]
        report.check(not hits, f"no {why}", f"line(s) {hits}")

    # ---- embedded shell ----------------------------------------------------------
    for name, step in steps:
        run = step.get("run")
        if not run:
            continue
        label = step.get("name", "<unnamed step>")
        with tempfile.NamedTemporaryFile("w", suffix=".sh", delete=False) as handle:
            handle.write(run)
            path = handle.name
        proc = subprocess.run(["bash", "-n", path], capture_output=True, text=True)
        Path(path).unlink()
        report.check(proc.returncode == 0, f"[{name}] bash -n: {label}",
                     proc.stderr.strip().splitlines()[-1] if proc.stderr else "")
        if len(run.strip().splitlines()) > 1:
            report.check(run.strip().startswith("set -euo pipefail"),
                         f"[{name}] opts into strict mode: {label}")
            # $GITHUB_PATH / $GITHUB_ENV only apply to *later* steps; a step that writes
            # them and then calls the tool by bare name is a guaranteed CI failure.
            wrote_path = ">> \"$GITHUB_PATH\"" in run or ">> $GITHUB_PATH" in run
            if wrote_path:
                tail_after = run.split("$GITHUB_PATH", 1)[1]
                report.check("kotlinc -version" not in tail_after,
                             f"[{name}] does not call a just-added PATH tool by bare name: {label}")

    return report.emit()


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
