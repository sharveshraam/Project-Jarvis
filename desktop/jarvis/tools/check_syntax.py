"""Syntax-only check of every C# file with tree-sitter (no .NET SDK needed).

This catches malformed code, not type errors: the real build runs in CI on windows-latest.
Usage: python tools/check_syntax.py   (needs: pip install tree-sitter tree-sitter-c-sharp)
"""
import pathlib
import sys

import tree_sitter_c_sharp
from tree_sitter import Language, Parser

ROOT = pathlib.Path(__file__).resolve().parent.parent
parser = Parser(Language(tree_sitter_c_sharp.language()))


def problems(node, out):
    if node.type == "ERROR" or node.is_missing:
        out.append(node)
        return
    if node.has_error:
        for child in node.children:
            problems(child, out)


failed = 0
count = 0
for path in sorted(ROOT.rglob("*.cs")):
    if any(part in ("bin", "obj") for part in path.parts):
        continue
    count += 1
    source = path.read_bytes()
    tree = parser.parse(source)
    if not tree.root_node.has_error:
        continue
    found = []
    problems(tree.root_node, found)
    for node in found[:5]:
        failed += 1
        line = node.start_point[0] + 1
        text = source.splitlines()[node.start_point[0]].decode("utf-8", "replace").strip()
        kind = "missing " + node.type if node.is_missing else "syntax error"
        print(f"{path.relative_to(ROOT)}:{line}: {kind}: {text[:140]}")

print(f"{count} files checked, {failed} problem(s)")
sys.exit(1 if failed else 0)
