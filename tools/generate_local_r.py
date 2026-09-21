#!/usr/bin/env python3
"""
Generate a local stand-in for the aapt2-produced `R` class.

The real `dev.jarvis.app.R` is generated during an Android build by aapt2 from
app/src/main/res. Without the Android SDK there is no aapt2, so Kotlin code that
references `R.string.foo` cannot be compiled offline - which would make it impossible to
type-check the app module locally.

This script produces an equivalent Kotlin object graph, with a stable integer id per
resource, into a build directory that is never committed and never used by Gradle:

    build/local-android/gen/dev/jarvis/app/R.kt

It reuses the exact same resource scanner as tools/validate_android.py, so a resource
name that does not exist simply will not be generated and the Kotlin compile then fails -
which is precisely the check we want.

Usage: python3 tools/generate_local_r.py [--out DIR]
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import validate_android as va  # noqa: E402

# Android resource ids are packed as 0xPPTTEEEE (package, type, entry). The package byte
# for an app is 0x7f. We keep the same shape so the generated ids look real and stay
# unique across types.
PACKAGE_ID = 0x7F


def build_type_map(resources: dict[str, set[str]]) -> dict[str, list[str]]:
    return {rtype: sorted(names) for rtype, names in sorted(resources.items()) if names}


def render(resources: dict[str, set[str]], package: str) -> str:
    lines = [
        "// GENERATED FILE - DO NOT EDIT, DO NOT COMMIT.",
        "// Produced by tools/generate_local_r.py as an offline stand-in for the R class",
        "// that aapt2 generates during a real Android build. Values are not meaningful;",
        "// only their uniqueness and their existence are.",
        "@file:Suppress(\"unused\", \"ClassName\", \"ObjectPropertyName\", "
        "\"ConstPropertyName\", \"PackageName\", \"MatchingDeclarationName\")",
        "",
        f"package {package}",
        "",
        "object R {",
    ]

    type_index = 1
    for rtype, names in build_type_map(resources).items():
        safe_type = rtype.replace("-", "_").replace(".", "_")
        lines.append(f"    object {safe_type} {{")
        for entry_index, name in enumerate(names):
            safe_name = name.replace(".", "_").replace("-", "_").replace(":", "_")
            value = (PACKAGE_ID << 24) | (type_index << 16) | entry_index
            lines.append(f"        const val {safe_name}: Int = 0x{value:08x}")
        lines.append("    }")
        lines.append("")
        type_index += 1

    lines.append("}")
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", default=str(va.REPO_ROOT / "build" / "local-android" / "gen"))
    parser.add_argument("--package", default="dev.jarvis.app")
    args = parser.parse_args()

    file_resources = va.collect_file_resources()
    values_resources, _ = va.collect_values_resources()

    resources: dict[str, set[str]] = {}
    for rtype, names in file_resources.items():
        resources.setdefault(rtype, set()).update(names)
    for rtype, names in values_resources.items():
        resources.setdefault(rtype, set()).update(names)
    resources.setdefault("id", set()).update(va.collect_ids())

    if va.PROBLEMS:
        print("resource scan reported problems; cannot generate a trustworthy R:")
        for problem in va.PROBLEMS:
            print(f"  - {problem}")
        return 1

    out_dir = Path(args.out) / args.package.replace(".", "/")
    out_dir.mkdir(parents=True, exist_ok=True)
    target = out_dir / "R.kt"
    target.write_text(render(resources, args.package), encoding="utf-8")

    total = sum(len(v) for v in resources.values())
    print(f"generated {target} ({len(resources)} resource type(s), {total} entries)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
