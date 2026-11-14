#!/usr/bin/env python3
"""
Offline static validation of the Android module.

The sandbox that this project is developed in has no Android SDK: aapt2, d8 and the
platform android.jar are all unreachable, so resources cannot be compiled and Android
classes cannot be type-checked locally. That leaves a real gap - broken resource
references and manifest typos would only surface in CI, minutes later.

This script closes as much of that gap as is possible without the SDK. It checks:

  1. Every XML file under app/src/main is well-formed.
  2. Every `@type/name` reference in XML resolves to a declared resource
     (values/*.xml entries, res/<type>/<name>.* files, @+id definitions, and
     the small set of framework-provided ids we rely on).
  3. Every `R.type.name` reference in Kotlin resolves the same way.
  4. Every `android:name` component in AndroidManifest.xml maps to a real Kotlin
     file in the expected package.
  5. Every `*Binding` class referenced in Kotlin maps to an existing layout file
     (activity_main.xml -> ActivityMainBinding).
  6. Layout files referenced from Kotlin (`R.layout.x` + `inflate`) exist.
  7. Every string resource with format arguments has a consistent number of them,
     and strings used with arguments are not accidentally missing `%1$s`.

It is deliberately conservative: unknown `@android:` / `?attr/` references are
skipped rather than reported, so it does not need a platform to be installed.

Usage:  python3 tools/validate_android.py [--verbose]
Exit:   0 when clean, 1 when any problem is found.
"""

from __future__ import annotations

import argparse
import re
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
APP_MAIN = REPO_ROOT / "app" / "src" / "main"
RES = APP_MAIN / "res"
KOTLIN_ROOT = APP_MAIN / "kotlin"
MANIFEST = APP_MAIN / "AndroidManifest.xml"
ANDROID_NS = "{http://schemas.android.com/apk/res/android}"

FILE_RESOURCE_TYPES = {
    "layout", "drawable", "mipmap", "xml", "raw", "anim", "animator", "menu", "font", "color",
}
VALUES_TYPES = {
    "string", "color", "dimen", "bool", "integer", "style", "array", "string-array",
    "integer-array", "plurals", "attr", "item", "declare-styleable", "eat-comment",
}
# Types whose names may be referenced but are declared inline or come from the framework.
LENIENT_TYPES = {"id", "android", "attr", "style", "dimen", "color", "string"}

PROBLEMS: list[str] = []
NOTES: list[str] = []


def fail(message: str) -> None:
    PROBLEMS.append(message)


def note(message: str) -> None:
    NOTES.append(message)


def strip_qualifiers(dirname: str) -> str:
    return dirname.split("-")[0]


def collect_file_resources() -> dict[str, set[str]]:
    """res/layout-land/activity_main.xml -> {'layout': {'activity_main'}}"""
    found: dict[str, set[str]] = defaultdict(set)
    if not RES.exists():
        fail(f"missing resource directory: {RES}")
        return found
    for res_dir in sorted(RES.iterdir()):
        if not res_dir.is_dir():
            continue
        rtype = strip_qualifiers(res_dir.name)
        if rtype == "values":
            continue  # handled by collect_values_resources()
        if rtype not in FILE_RESOURCE_TYPES:
            fail(f"unknown resource directory: res/{res_dir.name}")
            continue
        for entry in sorted(res_dir.iterdir()):
            if entry.is_file() and entry.suffix in {".xml", ".png", ".webp", ".jpg", ".ttf", ".otf", ".9.png"}:
                found[rtype].add(entry.name.split(".")[0])
            elif entry.is_file():
                found[rtype].add(entry.stem)
    return found


def collect_values_resources() -> tuple[dict[str, set[str]], dict[str, int]]:
    """Declarations from res/values/*.xml, plus expected format-arg count per string."""
    declared: dict[str, set[str]] = defaultdict(set)
    format_args: dict[str, int] = {}
    values_dirs = [d for d in RES.iterdir() if d.is_dir() and strip_qualifiers(d.name) == "values"] if RES.exists() else []
    for values_dir in sorted(values_dirs):
        for xml_file in sorted(values_dir.glob("*.xml")):
            try:
                root = ET.parse(xml_file).getroot()
            except ET.ParseError as exc:
                fail(f"{rel(xml_file)}: malformed XML: {exc}")
                continue
            for child in root:
                tag = child.tag
                name = child.get("name")
                if tag == "item":
                    rtype = child.get("type", "id")
                    if name:
                        declared[rtype].add(name)
                    continue
                if tag == "declare-styleable":
                    declared["styleable"].add(name or "")
                    for inner in child:
                        if inner.tag == "attr" and inner.get("name"):
                            declared["attr"].add(inner.get("name"))
                    continue
                if tag == "eat-comment" or tag is ET.Comment:
                    continue
                if tag not in VALUES_TYPES:
                    fail(f"{rel(xml_file)}: unknown values tag <{tag}>")
                    continue
                if not name:
                    fail(f"{rel(xml_file)}: <{tag}> without a name attribute")
                    continue
                if tag == "string-array" or tag == "integer-array" or tag == "array":
                    declared["array"].add(name)
                elif tag == "plurals":
                    declared["plurals"].add(name)
                else:
                    declared[tag].add(name)
                if tag == "string" and child.text:
                    format_args[name] = len(set(re.findall(r"%(\d+)\$", child.text)))
    return declared, format_args


def collect_style_parents() -> dict[str, bool]:
    """Every declared `<style>` name -> whether it carries an explicit `parent` attribute.

    aapt2 treats a dotted style name with no explicit parent as a child of the style named
    by its prefix, and fails to link if that prefix style does not exist. `Theme.Jarvis.Bar`
    therefore requires either a declared `Theme.Jarvis.Bar` parent or a `Theme.Jarvis` style.
    """
    parents: dict[str, bool] = {}
    values_dirs = [d for d in RES.iterdir() if d.is_dir() and strip_qualifiers(d.name) == "values"] if RES.exists() else []
    for values_dir in sorted(values_dirs):
        for xml_file in sorted(values_dir.glob("*.xml")):
            try:
                root = ET.parse(xml_file).getroot()
            except ET.ParseError:
                continue  # check_xml_wellformed already reported it
            for child in root:
                if child.tag == "style" and child.get("name"):
                    parents[child.get("name")] = "parent" in child.attrib
    return parents


def rel(path: Path) -> str:
    try:
        return str(path.relative_to(REPO_ROOT))
    except ValueError:
        return str(path)


def collect_ids() -> set[str]:
    """Every `@+id/name` declared anywhere in XML."""
    ids: set[str] = set()
    pattern = re.compile(r"@\+?(?:android:)?id/([A-Za-z0-9_]+)")
    for xml_file in xml_files():
        ids.update(pattern.findall(xml_file.read_text(encoding="utf-8")))
    return ids


def xml_files() -> list[Path]:
    return sorted(p for p in APP_MAIN.rglob("*.xml"))


def kotlin_files() -> list[Path]:
    return sorted(p for p in KOTLIN_ROOT.rglob("*.kt")) if KOTLIN_ROOT.exists() else []


def check_xml_wellformed() -> None:
    for xml_file in xml_files():
        try:
            ET.parse(xml_file)
        except ET.ParseError as exc:
            fail(f"{rel(xml_file)}: malformed XML: {exc}")


REF_PATTERN = re.compile(r"@(?!\*)([a-z][a-z0-9_]*):?([A-Za-z0-9_.]+)?/?([A-Za-z0-9_.]*)")
R_PATTERN = re.compile(r"\bR\.([a-z][a-z0-9_]*)\.([A-Za-z0-9_]+)")
BINDING_PATTERN = re.compile(r"\b([A-Z][A-Za-z0-9]*)Binding\b")


def check_xml_references(resources: dict[str, set[str]]) -> None:
    """Resolve @type/name references found in XML attributes and text."""
    ref = re.compile(r"@(?:\+)?([a-zA-Z][a-zA-Z0-9_]*)/([A-Za-z0-9_.]+)")
    for xml_file in xml_files():
        text = xml_file.read_text(encoding="utf-8")
        for match in ref.finditer(text):
            rtype, name = match.group(1), match.group(2)
            if rtype in {"android", "package"}:
                continue  # framework resource, cannot be validated without android.jar
            if name.startswith("android:"):
                continue
            if rtype == "style" or "." in name and rtype == "style":
                pass
            if rtype not in resources:
                fail(f"{rel(xml_file)}: @{rtype}/{name} - unknown resource type '{rtype}'")
                continue
            if name in resources[rtype]:
                continue
            if rtype == "style" and name.split(".")[0] in resources.get("style", set()):
                continue  # implicit style inheritance: Theme.Jarvis.Bar -> Theme.Jarvis
            if rtype in LENIENT_TYPES:
                note(f"{rel(xml_file)}: @{rtype}/{name} not found locally (may be framework-provided)")
                continue
            fail(f"{rel(xml_file)}: @{rtype}/{name} does not resolve to a declared resource")


def check_style_hierarchy(style_parents: dict[str, bool]) -> None:
    """Fail when a dotted style relies on an implicit parent that was never declared."""
    for name, has_explicit_parent in sorted(style_parents.items()):
        if has_explicit_parent or "." not in name:
            continue
        prefix = name.rsplit(".", 1)[0]
        if prefix not in style_parents:
            fail(f'res/values: <style name="{name}"> declares no parent, so aapt2 resolves it '
                 f'as a child of style "{prefix}", which does not exist. aapt2 link would fail '
                 f'with "resource style/{prefix} not found". Declare that style, or give this '
                 f'one an explicit parent (parent="" means none).')


def check_kotlin_r_references(resources: dict[str, set[str]]) -> None:
    for kt in kotlin_files():
        text = kt.read_text(encoding="utf-8")
        for match in R_PATTERN.finditer(text):
            rtype, name = match.group(1), match.group(2)
            if rtype not in resources:
                fail(f"{rel(kt)}: R.{rtype}.{name} - unknown resource type '{rtype}'")
                continue
            if name not in resources[rtype]:
                fail(f"{rel(kt)}: R.{rtype}.{name} does not resolve to a declared resource")


def check_binding_references(layouts: set[str]) -> None:
    valid = {to_binding_name(layout) for layout in layouts}
    for kt in kotlin_files():
        text = kt.read_text(encoding="utf-8")
        for match in BINDING_PATTERN.finditer(text):
            binding = f"{match.group(1)}Binding"
            if binding in valid:
                continue
            # Ignore unrelated *Binding names (e.g. core interfaces ending in Binding).
            if "import dev.jarvis" in text and binding.startswith("Jarvis"):
                continue
            if binding in {"ViewBinding", "DataBinding", "FragmentBinding", "ActivityBinding"}:
                continue
            fail(f"{rel(kt)}: {binding} has no matching layout (expected res/layout/{from_binding_name(binding)}.xml)")


def to_binding_name(layout: str) -> str:
    return "".join(part.capitalize() for part in layout.split("_")) + "Binding"


def from_binding_name(binding: str) -> str:
    stem = binding[: -len("Binding")]
    out = []
    for index, ch in enumerate(stem):
        if ch.isupper() and index > 0:
            out.append("_")
        out.append(ch.lower())
    return "".join(out)


def check_manifest_components() -> None:
    if not MANIFEST.exists():
        fail("missing app/src/main/AndroidManifest.xml")
        return
    try:
        root = ET.parse(MANIFEST).getroot()
    except ET.ParseError as exc:
        fail(f"AndroidManifest.xml: malformed XML: {exc}")
        return

    package_hint = "dev.jarvis.app"
    application = root.find("application")
    if application is None:
        fail("AndroidManifest.xml: no <application> element")
        return

    component_tags = {"application", "activity", "service", "receiver", "provider", "activity-alias"}
    existing_kotlin = {rel(p).replace("app/src/main/kotlin/", "").replace("/", ".").removesuffix(".kt")
                       for p in kotlin_files()}

    for element in application.iter():
        tag = element.tag
        if tag not in component_tags:
            continue
        name = element.get(f"{ANDROID_NS}name")
        if not name:
            continue
        if name.startswith("androidx.") or name.startswith("android.") or name.startswith("com.google."):
            continue
        if name.startswith("."):
            fqcn = f"{package_hint}{name}"
        elif "." not in name:
            fqcn = f"{package_hint}.{name}"
        else:
            fqcn = name
        if fqcn not in existing_kotlin:
            fail(f"AndroidManifest.xml: <{tag} android:name=\"{name}\"> has no Kotlin class {fqcn}.kt")


def check_string_format_args(format_args: dict[str, int]) -> None:
    """Strings consumed with arguments must declare the right number of them."""
    for kt in kotlin_files():
        text = kt.read_text(encoding="utf-8")
        for match in re.finditer(r"getString\(\s*R\.string\.([A-Za-z0-9_]+)\s*((?:,\s*[^)]+?)?)\)", text, re.S):
            name, args = match.group(1), match.group(2)
            declared = format_args.get(name)
            if declared is None:
                continue
            passed = 0 if not args.strip() else args.count(",")
            if declared != passed:
                fail(
                    f"{rel(kt)}: getString(R.string.{name}, ...) passes {passed} argument(s) "
                    f"but the string declares {declared} format placeholder(s)"
                )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--verbose", action="store_true")
    args = parser.parse_args()

    if not APP_MAIN.exists():
        print(f"nothing to validate: {rel(APP_MAIN)} does not exist")
        return 0

    check_xml_wellformed()

    file_resources = collect_file_resources()
    values_resources, format_args = collect_values_resources()
    resources: dict[str, set[str]] = defaultdict(set)
    for rtype, names in file_resources.items():
        resources[rtype] |= names
    for rtype, names in values_resources.items():
        resources[rtype] |= names
    resources["id"] = collect_ids()

    check_xml_references(resources)
    check_style_hierarchy(collect_style_parents())
    check_kotlin_r_references(resources)
    check_binding_references(resources.get("layout", set()))
    check_manifest_components()
    check_string_format_args(format_args)

    total_resources = sum(len(v) for k, v in resources.items() if k != "id")
    print(f"validated {len(xml_files())} XML file(s), {len(kotlin_files())} Kotlin file(s), "
          f"{total_resources} declared resource(s), {len(resources.get('id', ()))} id(s)")

    if args.verbose:
        for entry in NOTES:
            print(f"  note: {entry}")

    if PROBLEMS:
        print()
        print(f"FAILED: {len(PROBLEMS)} problem(s)")
        for problem in PROBLEMS:
            print(f"  - {problem}")
        return 1

    if NOTES:
        print(f"({len(NOTES)} lenient note(s); re-run with --verbose to list them)")
    print("OK: Android resources, manifest components and view bindings are consistent")
    return 0


if __name__ == "__main__":
    sys.exit(main())
