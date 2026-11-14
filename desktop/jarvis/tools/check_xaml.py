"""Best-effort XAML check (no .NET SDK needed).

Verifies, for every .xaml file of the desktop app:
  * the file is well-formed XML;
  * every {StaticResource key} is defined (and, inside a resource dictionary, defined earlier);
  * every {Binding path} names a public property of some view-model/row class;
  * bindings that write back (TwoWay, or the default on TextBox.Text / IsChecked / SelectedItem / Value)
    target a property with a public setter - WPF throws at run time otherwise;
  * every event handler named in XAML exists in the code-behind.
The real proof is the CI build plus `Jarvis.exe --smoke-test`.

Usage: python tools/check_xaml.py
"""
import pathlib
import re
import sys
import xml.dom.minidom
from collections import defaultdict

import tree_sitter_c_sharp
from tree_sitter import Language, Parser

ROOT = pathlib.Path(__file__).resolve().parent.parent
APP = ROOT / "src" / "Jarvis.Desktop"
PARSER = Parser(Language(tree_sitter_c_sharp.language()))
TWO_WAY_DEFAULT = {("TextBox", "Text"), ("CheckBox", "IsChecked"), ("RadioButton", "IsChecked"), ("ComboBox", "SelectedItem"),
                   ("ListBox", "SelectedItem"), ("Slider", "Value"), ("ToggleButton", "IsChecked"), ("ComboBox", "Text"),
                   ("ComboBox", "SelectedValue"), ("ListBox", "SelectedValue")}
EVENTS = {"Click", "PreviewKeyDown", "KeyDown", "Loaded", "Closing", "Closed", "MouseLeftButtonDown", "TextChanged",
          "SelectionChanged", "Checked", "Unchecked", "PasswordChanged", "MouseDown", "PreviewMouseDown", "SourceInitialized"}


def text(node):
    return node.text.decode("utf-8", "replace") if node is not None else ""


def collect_properties():
    """name -> True when some class exposes a public setter for it."""
    props = {}
    for path in sorted(ROOT.rglob("*.cs")):
        if any(part in ("bin", "obj") for part in path.parts):
            continue
        tree = PARSER.parse(path.read_bytes())
        stack = [tree.root_node]
        while stack:
            n = stack.pop()
            stack.extend(n.children)
            if n.type == "property_declaration":
                if not any(text(c) == "public" for c in n.children if c.type == "modifier"):
                    continue
                name = text(n.child_by_field_name("name"))
                settable = False
                accessors = n.child_by_field_name("accessors")
                if accessors is not None:
                    for a in accessors.named_children:
                        words = [text(c) for c in a.children]
                        if ("set" in words) and "private" not in words and "protected" not in words and "internal" not in words:
                            settable = True
                props[name] = props.get(name, False) or settable
            elif n.type == "record_declaration":
                for c in n.children:
                    if c.type == "parameter_list":
                        for p in c.named_children:
                            if p.type == "parameter":
                                props.setdefault(text(p.child_by_field_name("name")), False)
    return props


def collect_methods(path):
    names = set()
    if not path.exists():
        return names
    tree = PARSER.parse(path.read_bytes())
    stack = [tree.root_node]
    while stack:
        n = stack.pop()
        stack.extend(n.children)
        if n.type == "method_declaration":
            names.add(text(n.child_by_field_name("name")))
    return names


def keys_in_order(path):
    """Resource keys with the line they are declared on."""
    keys = {}
    for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        for m in re.finditer(r'x:Key="([^"]+)"', line):
            keys.setdefault(m.group(1), number)
    return keys


def main():
    findings = []
    props = collect_properties()
    files = sorted(APP.rglob("*.xaml"))
    global_keys = {}
    dictionaries = [p for p in files if "<ResourceDictionary" in p.read_text(encoding="utf-8")[:400] or p.name == "App.xaml"]
    for path in dictionaries:
        global_keys.update(keys_in_order(path))

    bindings = 0
    resources = 0
    for path in files:
        rel = path.relative_to(ROOT)
        source = path.read_text(encoding="utf-8")
        try:
            xml.dom.minidom.parseString(source.encode("utf-8"))
        except Exception as ex:  # noqa: BLE001
            findings.append(f"{rel}: not well-formed XML: {ex}")
            continue
        own_keys = keys_in_order(path)
        is_dictionary = path.name != "App.xaml" and "<ResourceDictionary" in source[:400]
        handlers = collect_methods(path.with_suffix(".xaml.cs"))
        element = None
        for number, line in enumerate(source.splitlines(), 1):
            tag = re.search(r"<([A-Za-z][\w.]*)", line)
            if tag:
                element = tag.group(1)
            for m in re.finditer(r"\{StaticResource ([^}\s,]+)\}", line):
                resources += 1
                key = m.group(1)
                declared = own_keys.get(key) if key in own_keys else global_keys.get(key)
                if declared is None:
                    findings.append(f"{rel}:{number}: StaticResource '{key}' is not defined")
                elif is_dictionary and key in own_keys and own_keys[key] >= number and f'x:Key="{key}"' not in line:
                    findings.append(f"{rel}:{number}: StaticResource '{key}' is used before it is defined (line {own_keys[key]})")
            for m in re.finditer(r'([\w.:]+)="\{Binding([^"]*)\}"', line):
                attribute, body = m.group(1), m.group(2).strip()
                bindings += 1
                if "ElementName=" in body or "TemplatedParent" in body:
                    continue
                first = body.split(",")[0].strip()
                if first.startswith("Path="):
                    first = first[5:]
                if first == "" or "=" in first:
                    continue
                segments = first.split(".")
                if segments[0] == "DataContext":
                    segments = segments[1:]
                for segment in segments:
                    if segment not in props:
                        findings.append(f"{rel}:{number}: binding path '{first}': no public property '{segment}'")
                leaf = segments[-1] if segments else None
                one_way = "Mode=OneWay" in body or "Mode=OneTime" in body
                writes = "Mode=TwoWay" in body or (not one_way and (element, attribute) in TWO_WAY_DEFAULT)
                if writes and leaf in props and not props[leaf]:
                    findings.append(f"{rel}:{number}: '{attribute}' writes back to '{leaf}', which has no public setter")
            for m in re.finditer(r'\b(\w+)="([A-Za-z_]\w*)"', line):
                if m.group(1) in EVENTS and m.group(2) not in handlers:
                    findings.append(f"{rel}:{number}: handler '{m.group(2)}' for {m.group(1)} is missing in the code-behind")

    for f in findings:
        print(f)
    print(f"{len(files)} XAML files, {bindings} bindings, {resources} resource references, {len(findings)} finding(s)")
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main())
