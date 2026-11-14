"""Checks tool names and argument names that are passed around as strings (no .NET SDK needed).

1. Collects every tool declared with Def("name", ...).Str(...).Int(...)... in the tool modules.
2. Inside each tool's own lambdas, every args.Str("x") / args.Int("x", ...) / ... must name a declared parameter.
3. Every locally built call - OfflineAssistant `new Intent("tool", Args(("k", v)))`, Workflows
   `Step("...", "tool", new JsonObject { ["k"] = v })` - must name a real tool, use only declared
   parameters, supply every required one, and use allowed enum values when the value is a literal.

Usage: python tools/check_tool_calls.py [-v]
"""
import pathlib
import sys

import tree_sitter_c_sharp
from tree_sitter import Language, Parser

ROOT = pathlib.Path(__file__).resolve().parent.parent
PARSER = Parser(Language(tree_sitter_c_sharp.language()))
DECLARERS = {"Str", "Int", "Bool", "StrArray", "Enum"}
READERS = {"Str", "Required", "Number", "Int", "Bool", "StrList"}


def text(node):
    return node.text.decode("utf-8", "replace") if node is not None else ""


def walk(node):
    stack = [node]
    while stack:
        n = stack.pop()
        yield n
        stack.extend(reversed(n.children))


def literal(node):
    """String literal value, or None when the node is not a plain string literal."""
    if node is None:
        return None
    if node.type == "argument":
        named = [c for c in node.named_children if c.type != "name_colon"]
        node = named[-1] if named else None
        if node is None:
            return None
    if node.type == "string_literal":
        raw = text(node)
        return raw[1:-1] if raw.startswith('"') else None
    return None


def arguments(invocation):
    arg_list = invocation.child_by_field_name("arguments")
    return [c for c in arg_list.named_children if c.type == "argument"] if arg_list is not None else []


def argument_name(argument):
    for c in argument.named_children:
        if c.type == "name_colon":
            return text(c).rstrip(":").strip()
    return None


def callee(invocation):
    """(receiver node or None, method name)."""
    function = invocation.child_by_field_name("function")
    if function is None:
        return None, ""
    if function.type == "member_access_expression":
        return function.child_by_field_name("expression"), text(function.child_by_field_name("name"))
    return None, text(function)


def string_array_fields(tree):
    """static readonly string[] Name = { "a", "b" } -> {Name: [a, b]}"""
    found = {}
    for n in walk(tree.root_node):
        if n.type == "variable_declarator":
            name = text(n.child_by_field_name("name")) or text(n.named_children[0])
            values = [literal(c) for c in walk(n) if c.type == "string_literal"]
            if values and all(v is not None for v in values) and any(c.type in ("initializer_expression", "array_creation_expression",
                                                                             "implicit_array_creation_expression") for c in walk(n)):
                found[name] = values
    return found


def collect_tools(findings):
    tools = {}
    for path in sorted((ROOT / "src").rglob("*.cs")):
        tree = PARSER.parse(path.read_bytes())
        arrays = string_array_fields(tree)
        for n in walk(tree.root_node):
            if n.type != "invocation_expression":
                continue
            receiver, name = callee(n)
            if name != "Def" or receiver is not None:
                continue
            args = arguments(n)
            tool_name = literal(args[0]) if args else None
            if tool_name is None:
                continue
            params = {}
            top = n
            while (top.parent is not None and top.parent.type == "member_access_expression"
                   and top.parent.parent is not None and top.parent.parent.type == "invocation_expression"):
                call = top.parent.parent
                _, method = callee(call)
                call_args = arguments(call)
                if method in DECLARERS and call_args:
                    pname = literal(call_args[0])
                    required = False
                    enum_values = None
                    if method == "Enum":
                        required = len(call_args) > 2 and text(call_args[2]).strip() == "true"
                        enum_values = []
                        for extra in call_args[3:]:
                            value = literal(extra)
                            if value is not None:
                                enum_values.append(value)
                            elif text(extra).strip() in arrays:
                                enum_values.extend(arrays[text(extra).strip()])
                            else:
                                enum_values = None
                                break
                    else:
                        for index, a in enumerate(call_args):
                            if argument_name(a) == "required" or (argument_name(a) is None and index == 2):
                                required = text(a).replace("required:", "").strip() == "true"
                    if pname is not None:
                        params[pname] = {"required": required, "enum": enum_values, "kind": method}
                top = call
            if tool_name in tools:
                findings.append(f"{path.relative_to(ROOT)}:{n.start_point[0] + 1}: tool '{tool_name}' is declared twice")
            tools[tool_name] = {"params": params, "file": path, "line": n.start_point[0] + 1}

            # Parameters read inside this tool's own lambdas.
            holder = top.parent
            while holder is not None and holder.type != "argument_list":
                holder = holder.parent
            if holder is None:
                continue
            for inner in walk(holder):
                if inner.type != "invocation_expression":
                    continue
                recv, method = callee(inner)
                if recv is None or method not in READERS or text(recv) not in ("args", "a"):
                    continue
                inner_args = arguments(inner)
                key = literal(inner_args[0]) if inner_args else None
                if key is not None and key not in params:
                    findings.append(f"{path.relative_to(ROOT)}:{inner.start_point[0] + 1}: tool '{tool_name}' reads argument "
                                    f"'{key}', which it does not declare")
    return tools


def check_call(where, tool_name, supplied, tools, findings, partial=False):
    if tool_name not in tools:
        findings.append(f"{where}: '{tool_name}' is not a declared tool")
        return
    params = tools[tool_name]["params"]
    for key, value in supplied.items():
        if key not in params:
            findings.append(f"{where}: '{tool_name}' has no parameter '{key}'")
            continue
        allowed = params[key]["enum"]
        if allowed and isinstance(value, str) and value not in allowed:
            findings.append(f"{where}: '{tool_name}.{key}' = \"{value}\" is not one of {allowed}")
    if not partial:
        for key, spec in params.items():
            if spec["required"] and key not in supplied:
                findings.append(f"{where}: '{tool_name}' requires '{key}'")


def tuple_pairs(node):
    """Args(("k", v), ...) -> {k: literal-or-True}"""
    pairs = {}
    for a in arguments(node):
        for c in a.named_children:
            if c.type == "tuple_expression":
                items = [x for x in c.named_children if x.type == "argument"]
                if len(items) == 2 and literal(items[0]) is not None:
                    value = literal(items[1])
                    pairs[literal(items[0])] = value if value is not None else True
    return pairs


def initializer_pairs(node):
    """new JsonObject { ["k"] = v } -> {k: literal-or-True}"""
    pairs = {}
    for n in walk(node):
        if n.type == "assignment_expression":
            left = n.child_by_field_name("left")
            right = n.child_by_field_name("right")
            if left is not None and (left.type == "element_access_expression" or text(left).lstrip().startswith("[")):
                keys = [literal(c) for c in walk(left) if c.type == "string_literal"]
                if keys and keys[0] is not None:
                    value = literal(right)
                    pairs[keys[0]] = value if value is not None else True
    return pairs


def main():
    verbose = "-v" in sys.argv
    findings = []
    tools = collect_tools(findings)
    calls = 0

    for path in sorted((ROOT / "src").rglob("*.cs")):
        tree = PARSER.parse(path.read_bytes())
        rel = path.relative_to(ROOT)
        for n in walk(tree.root_node):
            if n.type == "object_creation_expression" and text(n.child_by_field_name("type")) == "Intent":
                arg_list = n.child_by_field_name("arguments")
                args = [c for c in arg_list.named_children if c.type == "argument"] if arg_list is not None else []
                tool_name = literal(args[0]) if args else None
                if tool_name is None:
                    continue
                supplied = {}
                partial = True
                if len(args) > 1:
                    inner = [c for c in args[1].named_children if c.type == "invocation_expression"]
                    if inner and callee(inner[0])[1] == "Args":
                        supplied = tuple_pairs(inner[0])
                        partial = False
                calls += 1
                # `args["k"] = v` statements earlier in the same block also contribute.
                block = n.parent
                while block is not None and block.type != "block":
                    block = block.parent
                if partial and block is not None:
                    for inner in walk(block):
                        if inner.type == "invocation_expression" and callee(inner)[1] == "Args" and callee(inner)[0] is None:
                            supplied.update(tuple_pairs(inner))
                            partial = False
                    supplied.update(initializer_pairs(block))
                check_call(f"{rel}:{n.start_point[0] + 1}", tool_name, supplied, tools, findings, partial)
            elif n.type == "invocation_expression" and callee(n)[1] == "Step" and callee(n)[0] is None:
                args = arguments(n)
                if len(args) < 2 or literal(args[1]) is None:
                    continue
                calls += 1
                supplied = initializer_pairs(args[2]) if len(args) > 2 else {}
                check_call(f"{rel}:{n.start_point[0] + 1}", literal(args[1]), supplied, tools, findings)

    for f in findings:
        print(f)
    if verbose:
        for name in sorted(tools):
            spec = tools[name]["params"]
            print(f"  {name}({', '.join(k + ('*' if v['required'] else '') for k, v in spec.items())})")
    print(f"{len(tools)} tools declared, {calls} locally built calls checked, {len(findings)} finding(s)")
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main())
